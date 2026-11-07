namespace BaryoDev.Umbraco.SocialPost;

/// <summary>Bound from "BaryoDev:SocialPost". Network credentials are read separately from "BaryoDev:SocialPost:Networks:&lt;Name&gt;".</summary>
public sealed class SocialPostOptions
{
    public const string Section = "BaryoDev:SocialPost";
    public const string NetworksSection = Section + ":Networks";

    /// <summary>Master switch for the publish handler and the background service.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Queue content of these document types on first publish. Empty means none.</summary>
    public List<string> DocumentTypes { get; set; } = [];

    /// <summary>Property alias for the title. Empty uses the content name.</summary>
    public string TitleAlias { get; set; } = "";
    public string SummaryAlias { get; set; } = "";
    public string ImageAlias { get; set; } = "";
    public string TagsAlias { get; set; } = "";

    public bool PostOnFirstPublish { get; set; } = true;

    /// <summary>Used when the site cannot build an absolute url without a request, for example a relative media path.</summary>
    public string SiteBaseUrl { get; set; } = "";

    /// <summary>Extra hosts images may be fetched from, for example a media CDN. The site's own host is always allowed.</summary>
    public List<string> ImageHosts { get; set; } = [];

    public int MaxAttempts { get; set; } = 5;
    public int BaseDelaySeconds { get; set; } = 60;
    public int PollSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 10;
    public int HttpTimeoutSeconds { get; set; } = 30;
    public long MaxImageBytes { get; set; } = 5 * 1024 * 1024;
}
