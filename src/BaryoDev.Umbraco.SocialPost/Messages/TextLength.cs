using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BaryoDev.Umbraco.SocialPost.Messages;

/// <summary>How each network counts the length of a post.</summary>
public static partial class TextLength
{
    private const int LinkWeight = 23;

    /// <summary>Grapheme clusters (Bluesky, and a fair measure for LinkedIn).</summary>
    public static int Graphemes(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>Code points, with every link counted as 23 (Mastodon).</summary>
    public static int CodePointsWithLinks(string text)
    {
        int total = 0;
        int last = 0;
        foreach (Match m in Link().Matches(text))
        {
            total += CodePoints(text.AsSpan(last, m.Index - last)) + LinkWeight;
            last = m.Index + m.Length;
        }

        return total + CodePoints(text.AsSpan(last));
    }

    private static int CodePoints(ReadOnlySpan<char> s)
    {
        int n = 0;
        foreach (Rune _ in s.EnumerateRunes())
        {
            n++;
        }

        return n;
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Link();
}
