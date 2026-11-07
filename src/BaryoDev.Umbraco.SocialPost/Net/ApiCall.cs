using System.Text.Json;
using BaryoDev.Umbraco.SocialPost.Connectors;

namespace BaryoDev.Umbraco.SocialPost.Net;

/// <summary>Sends a request and reads a size-capped JSON body. Turns failures into SocialPostException.</summary>
internal static class ApiCall
{
    private const long MaxResponseBytes = 1024 * 1024;

    // create is true for the final call that publishes the post when the network has no idempotency key.
    // If that call times out or answers 5xx, the post may exist, so it is not retried.
    public static async Task<(HttpResponseMessage Response, JsonDocument? Json)> SendAsync(
        HttpClient client, HttpRequestMessage request, string network, CancellationToken ct,
        Func<JsonElement, bool>? temporaryBody = null, bool create = false)
    {
        // One timeout for the whole call, the body read included.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(client.Timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(30) : client.Timeout);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (HttpRequestException e)
        {
            // Failing to connect means nothing was sent. Anything later is unclear.
            bool notSent = e.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError;
            if (create && !notSent)
            {
                throw new SocialPostException($"{network}: {SocialPostException.UnknownOutcome}", false);
            }

            throw new SocialPostException($"{network} could not be reached.", true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw TimedOut(network, create);
        }

        byte[]? body;
        try
        {
            body = await BoundedRead.ReadAsync(response.Content, MaxResponseBytes, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            response.Dispose();
            throw TimedOut(network, create);
        }
        catch (IOException)
        {
            response.Dispose();
            throw TimedOut(network, create);
        }

        JsonDocument? json = null;
        if (body is { Length: > 0 })
        {
            try
            {
                json = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                // Not JSON. The status code still tells the story.
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            int code = (int)response.StatusCode;
            string detail = json is null ? "" : " " + Describe(json.RootElement);
            bool ambiguous = create && code is 408 or 425 or >= 500;
            bool retry = !ambiguous && (code is 408 or 425 or 429 or >= 500
                || (json is not null && temporaryBody?.Invoke(json.RootElement) == true));
            json?.Dispose();
            response.Dispose();
            string text = $"{network} answered HTTP {code}.{detail}".TrimEnd();
            throw new SocialPostException(ambiguous ? $"{text} {SocialPostException.UnknownOutcome}" : text, retry);
        }

        return (response, json);
    }

    private static SocialPostException TimedOut(string network, bool create) =>
        create
            ? new SocialPostException($"{network} timed out. {SocialPostException.UnknownOutcome}", false)
            : new SocialPostException($"{network} timed out.", true);

    /// <summary>Picks the usual error fields out of a JSON error body.</summary>
    private static string Describe(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        foreach (string key in new[] { "error", "message", "detail", "title", "error_description" })
        {
            if (!root.TryGetProperty(key, out JsonElement v))
            {
                continue;
            }

            if (v.ValueKind == JsonValueKind.String)
            {
                return v.GetString() ?? "";
            }

            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String)
            {
                return m.GetString() ?? "";
            }
        }

        return "";
    }

    public static string? Str(JsonDocument? doc, params string[] path)
    {
        if (doc is null)
        {
            return null;
        }

        JsonElement cur = doc.RootElement;
        foreach (string p in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur))
            {
                return null;
            }
        }

        return cur.ValueKind == JsonValueKind.String ? cur.GetString() : null;
    }

    /// <summary>Requires an absolute https url with no credentials in it.</summary>
    public static Uri RequireHttps(string value, string what)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
        {
            throw new SocialPostException($"{what} must be an https address.", false);
        }

        return uri;
    }
}
