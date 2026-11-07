namespace BaryoDev.Umbraco.SocialPost.Sharing;

/// <summary>Web share links for the dashboard's Post now box. The user finishes the post on the network.</summary>
public static class ShareLinks
{
    public static readonly IReadOnlyList<string> Networks = ["x", "facebook", "linkedin"];

    /// <summary>The share link for a network, or an empty string for a name that is not one. Values are URL-encoded.</summary>
    public static string For(string network, string text, string url)
    {
        string u = Uri.EscapeDataString(url);
        return network.ToLowerInvariant() switch
        {
            "x" => $"https://x.com/intent/tweet?text={Uri.EscapeDataString(text)}&url={u}",
            "facebook" => $"https://www.facebook.com/sharer/sharer.php?u={u}",
            "linkedin" => $"https://www.linkedin.com/sharing/share-offsite/?url={u}",
            _ => "",
        };
    }
}
