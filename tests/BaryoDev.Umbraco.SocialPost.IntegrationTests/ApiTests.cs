using System.Text.Json;
using BaryoDev.Umbraco.SocialPost.Api;
using BaryoDev.Umbraco.SocialPost.Credentials;
using BaryoDev.Umbraco.SocialPost.Queue;
using BaryoDev.Umbraco.SocialPost.Sharing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.AuthorizationStatus;
using Umbraco.Cms.Tests.Common.Testing;

namespace BaryoDev.Umbraco.SocialPost.IntegrationTests;

[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest)]
public class ApiTests : SocialPostFixture
{
    private SocialPostController Controller()
    {
        IUser user = SuperUser();
        var security = Stub.Make<IBackOfficeSecurity>(("get_CurrentUser", _ => user));
        return ActivatorUtilities.CreateInstance<SocialPostController>(Services, new FixedAccessor(security));
    }

    private static PostNowRequest Request(string json) =>
        JsonSerializer.Deserialize<PostNowRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Test]
    public async Task Post_now_refuses_a_type_outside_DocumentTypes()
    {
        CreateType("post");
        CreateType("page");
        IContent page = Publish("A page", "page");
        IContent post = Publish("A post", "post");
        Enable("Fake", ("Token", "fake-token"));
        SocialPostController controller = Controller();

        var refused = await controller.PostNow(new PostNowRequest(page.Key, null, false));

        Assert.That(refused, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(((ApiError)((BadRequestObjectResult)refused).Value!).Error, Does.Contain("DocumentTypes"));
        Assert.That(Repository.List(null, 100), Is.Empty);

        // The same call for a listed type goes through, so the refusal is about the type.
        var accepted = await controller.PostNow(new PostNowRequest(post.Key, null, false));
        var results = (List<EnqueueResult>)((OkObjectResult)accepted).Value!;
        Assert.That(results.Single().Outcome, Is.EqualTo("queued"));
        Assert.That(Repository.List(null, 100).Single().ContentKey, Is.EqualTo(post.Key));
    }

    [Test]
    public async Task Post_at_in_the_request_is_ignored()
    {
        CreateType("post");
        IContent post = Publish("A post", "post");
        Enable("Fake", ("Token", "fake-token"));
        string json = $$"""{"contentKey":"{{post.Key}}","networks":["Fake"],"postAt":"2099-01-01T00:00:00Z","force":false}""";

        var response = await Controller().PostNow(Request(json));

        Assert.That(response, Is.InstanceOf<OkObjectResult>());
        QueueRow row = Repository.List(null, 100).Single();
        Assert.That(row.NextAttemptUtc, Is.LessThan(Clock.GetUtcNow().UtcDateTime.AddMinutes(1)), "queued for now, not for 2099");
        Assert.That(row.Status, Is.EqualTo(QueueStatus.Pending));
    }

    [Test]
    public void The_network_list_shows_only_registered_connectors()
    {
        var ok = (OkObjectResult)Controller().Networks();
        string[] names = ((List<NetworkState>)ok.Value!).Select(n => n.Name).Order().ToArray();

        // Fake is the connector this test host registers, the way a Pro package would.
        Assert.That(names, Is.EqualTo(new[] { "Bluesky", "Fake", "LinkedIn", "Mastodon" }));
    }

    [Test]
    public async Task Share_returns_the_links_from_ShareLinks()
    {
        CreateType("post");
        IContent post = Publish("Hello & welcome", "post");

        var ok = (OkObjectResult)await Controller().Share(post.Key);
        var info = (ShareInfo)ok.Value!;

        Assert.That(info.Text, Is.EqualTo("Hello & welcome"));
        Assert.That(info.Url, Is.EqualTo("https://site.test/post"));
        Assert.That(info.Links.Keys, Is.EquivalentTo(new[] { "x", "facebook", "linkedin" }));
        foreach (string network in ShareLinks.Networks)
        {
            Assert.That(info.Links[network], Is.EqualTo(ShareLinks.For(network, "Hello & welcome", "https://site.test/post")));
        }

        Assert.That(info.Links["x"], Does.Contain("text=Hello%20%26%20welcome"));
    }

    [Test]
    public async Task Share_refuses_content_that_is_not_published()
    {
        CreateType("post");
        var draft = Content.Create("Draft", global::Umbraco.Cms.Core.Constants.System.Root, "post");
        Content.Save(draft);
        SocialPostController controller = Controller();

        Assert.That(await controller.Share(draft.Key), Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(await controller.Share(Guid.NewGuid()), Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task A_user_without_Browse_does_not_see_or_change_queue_rows()
    {
        CreateType("post");
        IContent post = Publish("A post", "post");
        Enable("Fake", ("Token", "fake-token"));
        await Controller().PostNow(new PostNowRequest(post.Key, null, false));
        QueueRow row = Repository.List(null, 100).Single();

        // A signed-in user in no group, whose Browse check on every node fails.
        IUser user = Stub.Make<IUser>();
        var security = Stub.Make<IBackOfficeSecurity>(("get_CurrentUser", _ => user));
        Func<object?[], object?> deny = _ => Task.FromResult(ContentAuthorizationStatus.UnauthorizedMissingPermissionAccess);
        var permissions = Stub.Make<IContentPermissionService>(("AuthorizeAccessAsync", deny));
        var limited = ActivatorUtilities.CreateInstance<SocialPostController>(Services, new FixedAccessor(security), permissions);

        var list = (List<QueueItem>)((OkObjectResult)await limited.Queue()).Value!;
        Assert.That(list, Is.Empty);
        Assert.That(await limited.Cancel(row.Id), Is.InstanceOf<NotFoundResult>());
        Assert.That(Repository.Get(row.Id)!.Status, Is.EqualTo(QueueStatus.Pending));

        var admin = (List<QueueItem>)((OkObjectResult)await Controller().Queue()).Value!;
        Assert.That(admin.Select(i => i.Id), Is.EqualTo(new[] { row.Id }));
    }

    private SocialPostController Limited(ContentAuthorizationStatus answer)
    {
        // A signed-in user in no group, so only the permission service decides.
        IUser user = Stub.Make<IUser>();
        var security = Stub.Make<IBackOfficeSecurity>(("get_CurrentUser", _ => user));
        var permissions = Stub.Make<IContentPermissionService>(("AuthorizeAccessAsync", _ => Task.FromResult(answer)));
        return ActivatorUtilities.CreateInstance<SocialPostController>(Services, new FixedAccessor(security), permissions);
    }

    [Test]
    public async Task A_user_without_Browse_cannot_retry_a_failed_row()
    {
        CreateType("post");
        IContent post = Publish("A post", "post");
        Enable("Fake", ("Token", "fake-token"));
        await Controller().PostNow(new PostNowRequest(post.Key, null, false));
        QueueRow row = Repository.List(null, 100).Single();
        Assert.That(Queue().Cancel(row.Id), Is.True);
        Assert.That(Repository.Get(row.Id)!.Status, Is.EqualTo(QueueStatus.Cancelled));

        var result = await Limited(ContentAuthorizationStatus.UnauthorizedMissingPermissionAccess).Retry(row.Id);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
        Assert.That(Repository.Get(row.Id)!.Status, Is.EqualTo(QueueStatus.Cancelled), "the row stays as it was");
        Assert.That(await Controller().Retry(row.Id), Is.InstanceOf<NoContentResult>(), "an administrator can still retry it");
    }

    [Test]
    public async Task Rows_of_deleted_content_are_listed_for_administrators_only()
    {
        CreateType("post");
        IContent post = Publish("A post", "post");
        Enable("Fake", ("Token", "fake-token"));
        await Controller().PostNow(new PostNowRequest(post.Key, null, false));
        Content.Delete(post);

        var admin = (List<QueueItem>)((OkObjectResult)await Controller().Queue()).Value!;
        Assert.That(admin, Has.Count.EqualTo(1));

        // Even a permission service that says yes cannot help, because the node is gone and the user is no administrator.
        var limited = (List<QueueItem>)((OkObjectResult)await Limited(ContentAuthorizationStatus.UnauthorizedMissingPermissionAccess).Queue()).Value!;
        Assert.That(limited, Is.Empty);
    }

    [Test]
    public async Task A_non_administrator_with_Browse_sees_and_changes_the_row()
    {
        CreateType("post");
        IContent post = Publish("A post", "post");
        Enable("Fake", ("Token", "fake-token"));
        await Controller().PostNow(new PostNowRequest(post.Key, null, false));
        QueueRow row = Repository.List(null, 100).Single();
        SocialPostController allowed = Limited(ContentAuthorizationStatus.Success);

        var list = (List<QueueItem>)((OkObjectResult)await allowed.Queue()).Value!;
        Assert.That(list.Select(i => i.Id), Is.EqualTo(new[] { row.Id }));
        Assert.That(await allowed.Cancel(row.Id), Is.InstanceOf<NoContentResult>());
        Assert.That(Repository.Get(row.Id)!.Status, Is.EqualTo(QueueStatus.Cancelled));
    }

    [Test]
    public async Task Retry_and_Cancel_answer_404_for_a_missing_row()
    {
        SocialPostController controller = Controller();

        Assert.That(await controller.Retry(9999), Is.InstanceOf<NotFoundResult>());
        Assert.That(await controller.Cancel(9999), Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Post_now_and_Share_answer_404_before_any_other_check_when_Browse_fails()
    {
        CreateType("post");
        CreateType("page");
        IContent page = Publish("A page", "page");
        var draft = Content.Create("Draft", global::Umbraco.Cms.Core.Constants.System.Root, "post");
        Content.Save(draft);
        Enable("Fake", ("Token", "fake-token"));
        SocialPostController limited = Limited(ContentAuthorizationStatus.UnauthorizedMissingPermissionAccess);

        // A wrong type and an unpublished draft would be 400 for a user who may browse them.
        Assert.That(await limited.PostNow(new PostNowRequest(page.Key, null, false)), Is.InstanceOf<NotFoundResult>());
        Assert.That(await limited.PostNow(new PostNowRequest(draft.Key, null, false)), Is.InstanceOf<NotFoundResult>());
        Assert.That(await limited.Share(draft.Key), Is.InstanceOf<NotFoundResult>());
        Assert.That(await limited.PostNow(new PostNowRequest(Guid.NewGuid(), null, false)), Is.InstanceOf<NotFoundResult>());
        Assert.That(Repository.List(null, 100), Is.Empty);
    }

    private QueueService Queue() => GetRequiredService<QueueService>();
}
