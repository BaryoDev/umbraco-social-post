using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using BaryoDev.Umbraco.SocialPost.Messages;
using BaryoDev.Umbraco.SocialPost.Net;

namespace BaryoDev.Umbraco.SocialPost.Connectors;

/// <summary>AT Protocol: createSession with an app password, uploadBlob for the image, createRecord with an external embed and a link facet.</summary>
public sealed class BlueskyConnector(HttpClient client, ImageFetcher images) : ISocialConnector
{
    // Bluesky rejects blobs over about 1 MB. A bigger image is left out and the link card has no thumbnail.
    private const int MaxBlobBytes = 950_000;

    public string Name => "Bluesky";
    public int MaxLength => 300;
    public int Measure(string text) => TextLength.Graphemes(text);
    public string DefaultTemplate => "{title}\n\n{url}";

    public IReadOnlyList<CredentialField> Fields { get; } =
    [
        new("Handle", "Handle (name.bsky.social)", false, true),
        new("AppPassword", "App password", true, true),
        new("ServiceUrl", "Service address", false, false, "https://bsky.social"),
    ];

    public async Task<PostResult> PostAsync(SocialMessage message, IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        Uri service = ApiCall.RequireHttps(settings.GetValueOrDefault("ServiceUrl") is { Length: > 0 } s ? s : "https://bsky.social", "The Bluesky service address");

        string did, jwt;
        using (var login = new HttpRequestMessage(HttpMethod.Post, new Uri(service, "/xrpc/com.atproto.server.createSession"))
        {
            Content = JsonContent.Create(new { identifier = settings["Handle"], password = settings["AppPassword"] }),
        })
        {
            var (r, j) = await ApiCall.SendAsync(client, login, Name, ct);
            using (r)
            using (j)
            {
                did = ApiCall.Str(j, "did") ?? throw new SocialPostException("Bluesky did not return a session.", false);
                jwt = ApiCall.Str(j, "accessJwt") ?? throw new SocialPostException("Bluesky did not return a session.", false);
            }
        }

        JsonNode? thumb = null;
        FetchedImage? image = await images.FetchAsync(message.ImageUrl, message.Url, ct);
        if (image is not null && image.Bytes.Length <= MaxBlobBytes)
        {
            var blob = new ByteArrayContent(image.Bytes);
            blob.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
            using var upload = new HttpRequestMessage(HttpMethod.Post, new Uri(service, "/xrpc/com.atproto.repo.uploadBlob")) { Content = blob };
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            try
            {
                var (r, j) = await ApiCall.SendAsync(client, upload, Name, ct);
                using (r)
                using (j)
                {
                    if (j is not null && j.RootElement.TryGetProperty("blob", out var el))
                    {
                        thumb = JsonNode.Parse(el.GetRawText());
                    }
                }
            }
            catch (SocialPostException e) when (!e.Retryable)
            {
                // The image is optional. Post the link card without it.
            }
        }

        var external = new JsonObject
        {
            ["uri"] = message.Url,
            ["title"] = message.Title,
            ["description"] = message.Summary ?? "",
        };
        if (thumb is not null)
        {
            external["thumb"] = thumb;
        }

        var record = new JsonObject
        {
            ["$type"] = "app.bsky.feed.post",
            ["text"] = message.Text,
            ["createdAt"] = DateTime.UtcNow.ToString("o"),
            ["embed"] = new JsonObject { ["$type"] = "app.bsky.embed.external", ["external"] = external },
        };

        // Facet offsets are UTF-8 byte positions, not character positions.
        int at = message.Text.IndexOf(message.Url, StringComparison.Ordinal);
        if (at >= 0)
        {
            int start = Encoding.UTF8.GetByteCount(message.Text.AsSpan(0, at));
            record["facets"] = new JsonArray(new JsonObject
            {
                ["index"] = new JsonObject { ["byteStart"] = start, ["byteEnd"] = start + Encoding.UTF8.GetByteCount(message.Url) },
                ["features"] = new JsonArray(new JsonObject { ["$type"] = "app.bsky.richtext.facet#link", ["uri"] = message.Url }),
            });
        }

        using var create = new HttpRequestMessage(HttpMethod.Post, new Uri(service, "/xrpc/com.atproto.repo.createRecord"))
        {
            Content = JsonContent.Create(new JsonObject { ["repo"] = did, ["collection"] = "app.bsky.feed.post", ["record"] = record }),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var (resp, json) = await ApiCall.SendAsync(client, create, Name, ct, create: true);
        using (resp)
        using (json)
        {
            string uri = ApiCall.Str(json, "uri") ?? throw new SocialPostException($"Bluesky did not return a post uri. {SocialPostException.UnknownOutcome}", false) { OutcomeUnknown = true };
            string rkey = uri[(uri.LastIndexOf('/') + 1)..];
            return new PostResult(uri, $"https://bsky.app/profile/{did}/post/{rkey}");
        }
    }
}
