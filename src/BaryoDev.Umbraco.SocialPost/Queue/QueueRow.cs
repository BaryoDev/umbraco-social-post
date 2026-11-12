using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace BaryoDev.Umbraco.SocialPost.Queue;

public static class QueueStatus
{
    public const string Pending = "pending";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>The network answered 2xx but gave no post id, so the post may exist. Retry refuses this status.</summary>
    public const string UnknownOutcome = "unknown";
}

/// <summary>One row per content item and network. The unique index on both is what keeps posting idempotent.</summary>
[TableName(TableName)]
[PrimaryKey("Id", AutoIncrement = true)]
[ExplicitColumns]
public sealed class QueueRow
{
    public const string TableName = "socialPostQueue";

    [Column("Id")]
    [PrimaryKeyColumn(AutoIncrement = true)]
    public int Id { get; set; }

    [Column("ContentKey")]
    [Index(IndexTypes.UniqueNonClustered, Name = "IX_socialPostQueue_ContentNetwork", ForColumns = "ContentKey,Network")]
    public Guid ContentKey { get; set; }

    [Column("Network")]
    [Length(32)]
    public string Network { get; set; } = string.Empty;

    [Column("Status")]
    [Length(16)]
    public string Status { get; set; } = QueueStatus.Pending;

    [Column("Attempts")]
    public int Attempts { get; set; }

    /// <summary>Bumped by force, so a repost gets its own idempotency key.</summary>
    [Column("Generation")]
    public int Generation { get; set; }

    [Column("NextAttemptUtc")]
    public DateTime NextAttemptUtc { get; set; }

    [Column("CreatedUtc")]
    public DateTime CreatedUtc { get; set; }

    [Column("UpdatedUtc")]
    public DateTime UpdatedUtc { get; set; }

    [Column("SentUtc")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public DateTime? SentUtc { get; set; }

    [Column("RemoteId")]
    [NullSetting(NullSetting = NullSettings.Null)]
    [Length(512)]
    public string? RemoteId { get; set; }

    [Column("RemoteUrl")]
    [NullSetting(NullSetting = NullSettings.Null)]
    [Length(1024)]
    public string? RemoteUrl { get; set; }

    [Column("LastError")]
    [NullSetting(NullSetting = NullSettings.Null)]
    [Length(500)]
    public string? LastError { get; set; }

    [Column("Title")]
    [Length(500)]
    public string Title { get; set; } = string.Empty;

    [Column("Url")]
    [Length(1024)]
    public string Url { get; set; } = string.Empty;

    [Column("Summary")]
    [NullSetting(NullSetting = NullSettings.Null)]
    [Length(2000)]
    public string? Summary { get; set; }

    [Column("ImageUrl")]
    [NullSetting(NullSetting = NullSettings.Null)]
    [Length(1024)]
    public string? ImageUrl { get; set; }

    [Column("Tags")]
    [NullSetting(NullSetting = NullSettings.Null)]
    [Length(1000)]
    public string? Tags { get; set; }
}

/// <summary>A credential field set from the dashboard. Secret values are stored protected.</summary>
[TableName(TableName)]
[PrimaryKey("Id", AutoIncrement = true)]
[ExplicitColumns]
public sealed class CredentialRow
{
    public const string TableName = "socialPostCredential";

    [Column("Id")]
    [PrimaryKeyColumn(AutoIncrement = true)]
    public int Id { get; set; }

    [Column("Network")]
    [Length(32)]
    [Index(IndexTypes.UniqueNonClustered, Name = "IX_socialPostCredential_NetworkField", ForColumns = "Network,Field")]
    public string Network { get; set; } = string.Empty;

    [Column("Field")]
    [Length(64)]
    public string Field { get; set; } = string.Empty;

    [Column("Value")]
    [SpecialDbType(SpecialDbTypes.NVARCHARMAX)]
    public string Value { get; set; } = string.Empty;

    [Column("Protected")]
    public bool Protected { get; set; }
}
