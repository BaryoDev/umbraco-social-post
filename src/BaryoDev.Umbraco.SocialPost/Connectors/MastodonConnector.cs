using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BaryoDev.Umbraco.SocialPost.Messages;
using BaryoDev.Umbraco.SocialPost.Net;

namespace BaryoDev.Umbraco.SocialPost.Connectors;

/// <summary>Mastodon: optional media upload, then POST /api/v1/statuses with an Idempotency-Key.</summary>
public sealed class MastodonConnector(HttpClient client, ImageFetcher images) : ISocialConnector
{
    public string Name => "Mastodon";
    public int MaxLength => 500;
    public int Measure(string text) => TextLength.CodePointsWithLinks(text);
    public string DefaultTemplate => "{title}\n\n{summary}\n\n{url}\n\n{tags}";

    public IReadOnlyList<CredentialField> Fields { get; } =
    [
        new("InstanceUrl", "Instance address (https://...)", false, true),
        new("AccessToken", "Access token", true, true),
        new("Visibility", "Visibility (public, unlisted)", false, false, "public"),
    ];

    public async Task<PostResult> PostAsync(SocialMessage message, IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        Uri instance = ApiCall.RequireHttps(settings["InstanceUrl"], "The Mastodon instance address");
        string token = settings["AccessToken"];
        string visibility = settings.GetValueOrDefault("Visibility") is { Length: > 0 } v ? v : "public";

        string? mediaId = null;
        FetchedImage? image = await images.FetchAsync(message.ImageUrl, message.Url, ct);
        if (image is not null)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(image.Bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
            form.Add(file, "file", "image");
            form.Add(new StringContent(message.Title), "description");
            using var upload = new HttpRequestMessage(HttpMethod.Post, new Uri(instance, "/api/v1/media")) { Content = form };
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                var (upResponse, upJson) = await ApiCall.SendAsync(client, upload, Name, ct);
                using (upResponse)
                using (upJson)
                {
                    mediaId = ApiCall.Str(upJson, "id");
                }
            }
            catch (SocialPostException e) when (!e.Retryable)
            {
                // The image is optional. Post the text without it.
            }
        }

        var body = new JsonObject { ["status"] = message.Text, ["visibility"] = visibility };
        if (mediaId is not null)
        {
            body["media_ids"] = new JsonArray(mediaId);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(instance, "/api/v1/statuses")) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", message.IdempotencyKey);
        var (response, json) = await ApiCall.SendAsync(client, request, Name, ct);
        using (response)
        using (json)
        {
            string id = ApiCall.Str(json, "id") ?? throw new SocialPostException("Mastodon did not return a post id.", false);
            return new PostResult(id, ApiCall.Str(json, "url"));
        }
    }
}
