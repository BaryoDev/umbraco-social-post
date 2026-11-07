using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Core;

namespace BaryoDev.Umbraco.SocialPost.Publishing;

public sealed record Snapshot(string Title, string Url, string? Summary, string? ImageUrl, string? Tags);

/// <summary>Reads title, link, summary, image and tags from the published values of content, using the aliases in the options.</summary>
public sealed partial class ContentSnapshotReader(
    IOptions<SocialPostOptions> options,
    IPublishedUrlProvider urlProvider,
    IUmbracoContextFactory umbracoContextFactory,
    IMediaService media,
    ILanguageService languages)
{
    // The link comes from the published cache, so at first publish it can be empty. The queue worker resolves it again when it sends.
    public Snapshot? Read(IContent content)
    {
        SocialPostOptions o = options.Value;
        string? culture = PickCulture(content);

        string title = Text(content, o.TitleAlias, culture)
            ?? (culture is null ? content.PublishName : content.GetPublishName(culture))
            ?? content.Name ?? "";
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // The worker resolves the link again when it sends, so a missing or long one is not an error here.
        string url = ResolveUrl(content.Key) ?? "";
        if (url.Length > 1024)
        {
            url = "";
        }

        return new Snapshot(
            Cut(title, 500)!, url,
            Cut(Plain(Text(content, o.SummaryAlias, culture)), 2000),
            Cut(ImageUrl(content, o.ImageAlias, culture, o.SiteBaseUrl), 1024),
            Cut(Tags(content, o.TagsAlias, culture), 1000));
    }

    /// <summary>The absolute link of published content, or null when it has none yet.</summary>
    public string? ResolveUrl(Guid key)
    {
        // The queue worker and scheduled publishing run outside a web request, where there is no Umbraco context.
        using UmbracoContextReference reference = umbracoContextFactory.EnsureUmbracoContext();
        string url = urlProvider.GetUrl(key, UrlMode.Absolute, null);
        if (url.StartsWith('/') && Uri.TryCreate(options.Value.SiteBaseUrl, UriKind.Absolute, out Uri? b))
        {
            url = new Uri(b, url).ToString();
        }

        // "#" means the content has no published url.
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && u.Scheme is "http" or "https" ? url : null;
    }

    private string? PickCulture(IContent content)
    {
        if (!content.ContentType.Variations.HasFlag(ContentVariation.Culture))
        {
            return null;
        }

        string? def = languages.GetDefaultIsoCodeAsync().GetAwaiter().GetResult();
        return def is not null && content.PublishedCultures.Contains(def, StringComparer.OrdinalIgnoreCase)
            ? def
            : content.PublishedCultures.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    // Published value of a property. Invariant properties are read without a culture even on a variant type.
    private static object? Value(IContent content, string alias, string? culture)
    {
        IProperty? property = content.Properties.FirstOrDefault(p => p.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
        if (property is null)
        {
            return null;
        }

        string? c = property.PropertyType.Variations.HasFlag(ContentVariation.Culture) ? culture : null;
        return property.GetValue(c, null, published: true);
    }

    private static string? Text(IContent content, string alias, string? culture)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        string? raw = Value(content, alias, culture)?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Rich text is stored as {"markup": "...", "blocks": ...}.
        if (raw.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("markup", out JsonElement markup))
                {
                    return markup.GetString();
                }
            }
            catch (JsonException)
            {
                // Not JSON after all; use it as text.
            }
        }

        return raw;
    }

    private string? ImageUrl(IContent content, string alias, string? culture, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        string? raw = Value(content, alias, culture)?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string? candidate = raw;
        if (raw.TrimStart().StartsWith('['))
        {
            // Media picker: [{"key": "...", "mediaKey": "..."}]
            try
            {
                using var doc = JsonDocument.Parse(raw);
                JsonElement first = doc.RootElement.EnumerateArray().FirstOrDefault();
                string? mediaKey = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("mediaKey", out JsonElement k) ? k.GetString() : null;
                candidate = Guid.TryParse(mediaKey, out Guid g) ? MediaFile(g) : null;
            }
            catch (JsonException)
            {
                candidate = null;
            }
        }
        else if (Guid.TryParse(raw, out Guid mediaGuid))
        {
            candidate = MediaFile(mediaGuid);
        }

        if (candidate is null)
        {
            return null;
        }

        if (candidate.StartsWith('/') && Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? b))
        {
            candidate = new Uri(b, candidate).ToString();
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out Uri? u) && u.Scheme is "http" or "https" ? candidate : null;
    }

    private string? MediaFile(Guid key)
    {
        IMedia? item = media.GetById(key);
        string? file = item?.GetValue<string>("umbracoFile");
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        // The upload field stores {"src": "/media/..."} or a plain path.
        if (file.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(file);
                return doc.RootElement.TryGetProperty("src", out JsonElement src) ? src.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return file;
    }

    private static string? Tags(IContent content, string alias, string? culture)
    {
        string? raw = Text(content, alias, culture);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (raw.TrimStart().StartsWith('['))
        {
            try
            {
                return string.Join(',', JsonSerializer.Deserialize<string[]>(raw) ?? []);
            }
            catch (JsonException)
            {
                return raw;
            }
        }

        return raw;
    }

    private static string? Plain(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        string text = System.Net.WebUtility.HtmlDecode(HtmlTag().Replace(html, " "));
        text = Spaces().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
