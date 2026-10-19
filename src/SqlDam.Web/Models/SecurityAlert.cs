namespace SqlDam.Web.Models;

public enum AlertStatus
{
    New,
    Acknowledged,
    InInvestigation,
    Resolved,
    FalsePositive
}

public sealed class SecurityAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid SourceId { get; set; }

    public string SourceName { get; set; } = string.Empty;

    public Guid? RuleId { get; set; }

    public string RuleName { get; set; } = string.Empty;

    public AlertSeverity Severity { get; set; } = AlertSeverity.Medium;

    public AlertStatus Status { get; set; } = AlertStatus.New;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int TriggerEventCount { get; set; } = 1;

    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ResolvedAtUtc { get; set; }

    public string? ResolvedBy { get; set; }

    public string? ResolutionNotes { get; set; }
}
