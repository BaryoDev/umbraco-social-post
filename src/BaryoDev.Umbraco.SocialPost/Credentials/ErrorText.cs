using System.Text.RegularExpressions;

namespace BaryoDev.Umbraco.SocialPost.Credentials;

/// <summary>Makes an error safe to store and show: credential values removed, one line, cut to length.</summary>
public static partial class ErrorText
{
    public const int MaxLength = 500;

    public static string Clean(string? text, IEnumerable<string> secrets)
    {
        string result = text ?? "";
        foreach (string s in secrets.Where(s => s.Length >= 4).OrderByDescending(s => s.Length))
        {
            result = result.Replace(s, "***", StringComparison.Ordinal);
        }

        result = BearerPattern().Replace(result, "Bearer ***");
        result = TokenPattern().Replace(result, "$1=***");
        result = Whitespace().Replace(result, " ").Trim();
        return result.Length <= MaxLength ? result : result[..(MaxLength - 3)] + "...";
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/=]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(access_token|refresh_token|client_secret|password)=[^&\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
