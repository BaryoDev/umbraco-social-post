namespace BaryoDev.Umbraco.SocialPost.Connectors;

public sealed record CredentialField(string Key, string Label, bool Secret, bool Required, string? Default = null);

/// <summary>What a connector sends. Text is already built from the template and trimmed.</summary>
public sealed record SocialMessage(string Text, string Title, string Url, string? Summary, string? ImageUrl, string IdempotencyKey);

public sealed record PostResult(string RemoteId, string? RemoteUrl);

public interface ISocialConnector
{
    string Name { get; }

    /// <summary>Length limit for the post text.</summary>
    int MaxLength { get; }

    /// <summary>Length of a text the way this network counts it.</summary>
    int Measure(string text);

    string DefaultTemplate { get; }

    /// <summary>Credential fields. "Enabled" and "Template" are handled for every network and are not listed here.</summary>
    IReadOnlyList<CredentialField> Fields { get; }

    Task<PostResult> PostAsync(SocialMessage message, IReadOnlyDictionary<string, string> settings, CancellationToken ct);
}

/// <summary>A failed post. Retryable errors are tried again with backoff, the rest fail the row at once.</summary>
public sealed class SocialPostException(string message, bool retryable) : Exception(message)
{
    public bool Retryable { get; } = retryable;

    public const string UnknownOutcome = "Unknown outcome: the request may have gone out. Check the network before retrying.";
}
