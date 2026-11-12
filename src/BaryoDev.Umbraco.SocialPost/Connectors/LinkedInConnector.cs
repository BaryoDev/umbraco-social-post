using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using BaryoDev.Umbraco.SocialPost.Messages;
using BaryoDev.Umbraco.SocialPost.Net;

namespace BaryoDev.Umbraco.SocialPost.Connectors;

/// <summary>LinkedIn Posts API (/rest/posts) with an article attachment. The author is the member (personal) URN.</summary>
public sealed class LinkedInConnector(HttpClient client) : ISocialConnector
{
    private const string MemberPrefix = "urn:li:person:";

    public string Name => "LinkedIn";
    public int MaxLength => 3000;
    public int Measure(string text) => TextLength.Graphemes(text);
    public string DefaultTemplate => "{title}\n\n{summary}\n\n{tags}";

    public IReadOnlyList<CredentialField> Fields { get; } =
    [
        new("AuthorUrn", "Member URN (urn:li:person:...)", false, true),
        new("AccessToken", "Access token", true, true),
        new("ApiVersion", "LinkedIn-Version header (YYYYMM)", false, false, "202601"),
    ];

    public async Task<PostResult> PostAsync(SocialMessage message, IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        string version = settings.GetValueOrDefault("ApiVersion") is { Length: > 0 } v ? v : "202601";
        string author = settings["AuthorUrn"].Trim();
        if (!author.StartsWith(MemberPrefix, StringComparison.Ordinal))
        {
            throw new SocialPostException("The LinkedIn author must be a member URN (urn:li:person:...).", false);
        }

        var body = new JsonObject
        {
            ["author"] = author,
            ["commentary"] = Escape(message.Text),
            ["visibility"] = "PUBLIC",
            ["distribution"] = new JsonObject
            {
                ["feedDistribution"] = "MAIN_FEED",
                ["targetEntities"] = new JsonArray(),
                ["thirdPartyDistributionChannels"] = new JsonArray(),
            },
            ["content"] = new JsonObject
            {
                ["article"] = new JsonObject
                {
                    ["source"] = message.Url,
                    ["title"] = message.Title,
                    ["description"] = message.Summary ?? "",
                },
            },
            ["lifecycleState"] = "PUBLISHED",
            ["isReclickedByAuthor"] = false,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.linkedin.com/rest/posts")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings["AccessToken"]);
        request.Headers.Add("LinkedIn-Version", version);
        request.Headers.Add("X-Restli-Protocol-Version", "2.0.0");

        var (response, json) = await ApiCall.SendAsync(client, request, Name, ct, create: true);
        using (response)
        using (json)
        {
            string? urn = response.Headers.TryGetValues("x-restli-id", out var ids) ? ids.FirstOrDefault() : null;
            if (string.IsNullOrEmpty(urn))
            {
                throw new SocialPostException($"LinkedIn did not return a post id. {SocialPostException.UnknownOutcome}", false) { OutcomeUnknown = true };
            }

            return new PostResult(urn, $"https://www.linkedin.com/feed/update/{Uri.EscapeDataString(urn)}");
        }
    }

    /// <summary>The commentary field is "little text": these characters start markup and need a backslash.</summary>
    internal static string Escape(string text)
    {
        const string reserved = "\\|{}@[]()<>#*_~";
        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (reserved.Contains(c))
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
