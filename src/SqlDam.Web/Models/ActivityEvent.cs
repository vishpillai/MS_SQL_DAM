namespace SqlDam.Web.Models;

/// <summary>
/// Categories normalized audit-feed events use so the UI can group and prioritize
/// them without re-parsing each provider's raw payload.
/// </summary>
public enum ActivityCategory
{
    LoginSuccess,
    LoginFailure,
    Error,
    SchemaChange,
    PermissionChange,
    PrincipalChange,
    DataChange,
    Other
}

/// <summary>A single security-relevant event captured from a monitored database source.</summary>
public sealed class ActivityEvent
{
    public long Id { get; set; }

    public Guid TenantId { get; set; } = TenantConstants.DefaultTenantId;

    public Guid SourceId { get; set; }

    public string SourceName { get; set; } = string.Empty;

    public string EventName { get; set; } = string.Empty;

    public ActivityCategory Category { get; set; } = ActivityCategory.Other;

    public DateTime TimestampUtc { get; set; }

    public string? LoginName { get; set; }

    public string? ClientHostName { get; set; }

    public string? ClientAppName { get; set; }

    public string? DatabaseName { get; set; }

    public string? ObjectName { get; set; }

    public string Summary { get; set; } = string.Empty;

    /// <summary>Raw provider audit payload, kept for investigation.</summary>
    public string DetailXml { get; set; } = string.Empty;
}
