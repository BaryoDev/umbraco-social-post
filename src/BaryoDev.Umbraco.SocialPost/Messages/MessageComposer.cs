using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BaryoDev.Umbraco.SocialPost.Connectors;

namespace BaryoDev.Umbraco.SocialPost.Messages;

/// <summary>Fills {title} {url} {summary} {tags} and trims to the network limit. The link is never cut.</summary>
public static partial class MessageComposer
{
    public static string Compose(string template, ISocialConnector connector, string title, string url, string? summary, string? tags)
    {
        string sum = summary ?? "";
        string tg = HashTags(tags);
        string ti = title;

        // One pass, so a title that contains "{url}" is not expanded a second time.
        string Render() => Tidy(Placeholder().Replace(template, m => m.Groups[1].Value switch
        {
            "title" => ti,
            "url" => url,
            "summary" => sum,
            _ => tg,
        }));

        string text = Render();
        int excess = connector.Measure(text) - connector.MaxLength;
        while (excess > 0 && sum.Length > 0)
        {
            sum = Shorten(sum, Count(sum) - excess);
            text = Render();
            excess = connector.Measure(text) - connector.MaxLength;
        }

        if (excess > 0 && tg.Length > 0)
        {
            tg = "";
            text = Render();
            excess = connector.Measure(text) - connector.MaxLength;
        }

        while (excess > 0 && ti.Length > 0)
        {
            ti = Shorten(ti, Count(ti) - excess);
            text = Render();
            excess = connector.Measure(text) - connector.MaxLength;
        }

        // Only a template that is itself too long gets here. Cut the end rather than send nothing.
        while (excess > 0 && text.Length > 1)
        {
            text = Shorten(text, Count(text) - excess);
            excess = connector.Measure(text) - connector.MaxLength;
        }

        return text;
    }

    private static int Count(string s) => new StringInfo(s).LengthInTextElements;

    /// <summary>Keeps the first (keep - 1) characters and adds an ellipsis. Empty when there is no room.</summary>
    private static string Shorten(string s, int keep)
    {
        if (keep <= 1)
        {
            return "";
        }

        var info = new StringInfo(s);
        return info.SubstringByTextElements(0, Math.Min(keep - 1, info.LengthInTextElements - 1)).TrimEnd() + "\u2026";
    }

    private static string HashTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return "";
        }

        var parts = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => NonWord().Replace(t, ""))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => "#" + t);
        return string.Join(' ', parts);
    }

    private static string Tidy(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd());
        var sb = new StringBuilder();
        int blank = 0;
        foreach (string line in lines)
        {
            blank = line.Length == 0 ? blank + 1 : 0;
            if (blank > 1)
            {
                continue;
            }

            sb.Append(line).Append('\n');
        }

        return sb.ToString().Trim();
    }

    [GeneratedRegex(@"\{(title|url|summary|tags)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"[^\p{L}\p{N}_]")]
    private static partial Regex NonWord();
}
