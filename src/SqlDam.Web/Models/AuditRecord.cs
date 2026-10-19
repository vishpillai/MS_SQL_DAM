namespace SqlDam.Web.Models;

/// <summary>
/// Tamper-evident, cryptographically chained audit record.
/// Each record contains a cryptographic SHA-256 hash of its contents
/// chained to the preceding record's hash (block-chain style integrity),
/// ensuring compliance with RBI Master Directions on IT Governance & Cybersecurity.
/// </summary>
public sealed class AuditRecord
{
    public long Sequence { get; set; }

    public Guid TenantId { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User, service account, or system component performing the action.
    /// </summary>
    public string Actor { get; set; } = "system";

    /// <summary>
    /// Specific action performed: e.g. "SOURCE_CREATED", "ALERT_RESOLVED", "RULE_MODIFIED", "EXPORT_GENERATED".
    /// </summary>
    public string Action { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    public string? EntityId { get; set; }

    public string Details { get; set; } = string.Empty;

    public string? ClientIp { get; set; }

    /// <summary>
    /// SHA-256 hash of the immediately preceding audit record.
    /// "GENESIS" for the first entry.
    /// </summary>
    public string PreviousHash { get; set; } = "GENESIS";

    /// <summary>
    /// Cryptographic SHA-256 hash of this record combined with <see cref="PreviousHash"/>.
    /// </summary>
    public string RecordHash { get; set; } = string.Empty;
}
