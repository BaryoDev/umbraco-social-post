using Asp.Versioning;
using BaryoDev.Umbraco.SocialPost.Connectors;
using BaryoDev.Umbraco.SocialPost.Credentials;
using BaryoDev.Umbraco.SocialPost.Publishing;
using BaryoDev.Umbraco.SocialPost.Queue;
using BaryoDev.Umbraco.SocialPost.Sharing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.AuthorizationStatus;
using Umbraco.Cms.Web.Common.Authorization;

namespace BaryoDev.Umbraco.SocialPost.Api;

public sealed record ApiError(string Error);

public sealed record SaveNetworkRequest(bool? Enabled, Dictionary<string, string?>? Values, List<string>? Clear);

public sealed record PostNowRequest(Guid ContentKey, List<string>? Networks, bool Force);

public sealed record ShareInfo(string Text, string Url, Dictionary<string, string> Links);

public sealed record QueueItem(
    int Id, Guid ContentKey, string Network, string Status, int Attempts, DateTime NextAttemptUtc, DateTime CreatedUtc,
    DateTime? SentUtc, string? RemoteUrl, string? LastError, string Title, string Url);

/// <summary>Networks, queue and post now. Settings section only, checked on the server. No response carries a stored secret.</summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("social-post")]
[ApiExplorerSettings(GroupName = "Social Post")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
[PlainServerErrors]
public sealed class SocialPostController(
    IEnumerable<ISocialConnector> connectors,
    CredentialStore credentials,
    QueueRepository repository,
    QueueService queue,
    ContentSnapshotReader reader,
    IContentService contentService,
    IContentPermissionService permissions,
    IBackOfficeSecurityAccessor security,
    IOptions<SocialPostOptions> options) : ManagementApiControllerBase
{
    [HttpGet("networks")]
    [ProducesResponseType<List<NetworkState>>(StatusCodes.Status200OK)]
    public IActionResult Networks() => Ok(connectors.Select(credentials.Describe).ToList());

    [HttpPut("networks/{name}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Save(string name, [FromBody] SaveNetworkRequest request)
    {
        ISocialConnector? connector = Find(name);
        if (connector is null)
        {
            return NotFound();
        }

        try
        {
            credentials.Save(connector, request.Enabled, request.Values ?? [], request.Clear ?? []);
        }
        catch (ArgumentException e)
        {
            return BadRequest(new ApiError(e.Message));
        }

        return NoContent();
    }

    [HttpGet("queue")]
    [ProducesResponseType<List<QueueItem>>(StatusCodes.Status200OK)]
    public IActionResult Queue([FromQuery] string? status = null, [FromQuery] int take = 100) =>
        Ok(repository.List(string.IsNullOrWhiteSpace(status) ? null : status, Math.Clamp(take, 1, 500)).Select(ToItem).ToList());

    [HttpPost("queue/{id:int}/retry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public IActionResult Retry(int id) =>
        queue.Retry(id) ? NoContent() : Conflict(new ApiError("Only failed or cancelled rows can be retried."));

    [HttpPost("queue/{id:int}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public IActionResult Cancel(int id) =>
        queue.Cancel(id) ? NoContent() : Conflict(new ApiError("Only pending rows can be cancelled."));

    [HttpPost("post-now")]
    [ProducesResponseType<List<EnqueueResult>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PostNow([FromBody] PostNowRequest request)
    {
        if (!options.Value.Enabled)
        {
            return BadRequest(new ApiError("Social post is turned off in configuration."));
        }

        IContent? content = contentService.GetById(request.ContentKey);
        if (content is null)
        {
            return NotFound();
        }

        if (!content.Published || content.Trashed)
        {
            return BadRequest(new ApiError("Publish the content first."));
        }

        if (!options.Value.DocumentTypes.Contains(content.ContentType.Alias, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new ApiError("This document type is not in the DocumentTypes setting."));
        }

        if (!await CanBrowseAsync(content))
        {
            return Forbid();
        }

        var networks = request.Networks is { Count: > 0 } ? request.Networks : null;
        if (networks is not null && networks.Any(n => Find(n) is null))
        {
            return BadRequest(new ApiError("Unknown network."));
        }

        return Ok(queue.Enqueue(content, networks, request.Force, explicitRequest: true));
    }

    /// <summary>Message and share links for published content, so the dashboard can open a network's share page.</summary>
    [HttpGet("share/{contentKey:guid}")]
    [ProducesResponseType<ShareInfo>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Share(Guid contentKey)
    {
        IContent? content = contentService.GetById(contentKey);
        if (content is null)
        {
            return NotFound();
        }

        if (!content.Published || content.Trashed)
        {
            return BadRequest(new ApiError("Publish the content first."));
        }

        if (!await CanBrowseAsync(content))
        {
            return Forbid();
        }

        string? url = reader.ResolveUrl(content.Key);
        Snapshot? snap = reader.Read(content);
        if (url is null || snap is null)
        {
            return BadRequest(new ApiError("The content has no title or published link yet."));
        }

        var links = ShareLinks.Networks.ToDictionary(n => n, n => ShareLinks.For(n, snap.Title, url));
        return Ok(new ShareInfo(snap.Title, url, links));
    }

    private async Task<bool> CanBrowseAsync(IContent content)
    {
        IUser? user = security.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return false;
        }

        ContentAuthorizationStatus status = await permissions.AuthorizeAccessAsync(user, content.Key, ActionBrowse.ActionLetter.ToString());
        return status == ContentAuthorizationStatus.Success;
    }

    private ISocialConnector? Find(string name) =>
        connectors.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static QueueItem ToItem(QueueRow r) => new(
        r.Id, r.ContentKey, r.Network, r.Status, r.Attempts, Utc(r.NextAttemptUtc), Utc(r.CreatedUtc),
        r.SentUtc is DateTime s ? Utc(s) : null, r.RemoteUrl, r.LastError, r.Title, r.Url);

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
