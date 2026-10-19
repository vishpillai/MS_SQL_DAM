namespace SqlDam.Web.Models;

public enum AlertSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public enum RuleCategory
{
    BruteForceLogin,
    PrivilegedAccountAccess,
    SchemaTampering,
    PermissionTampering,
    SevereErrorThreshold
}

public sealed class AlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public RuleCategory Category { get; set; }

    public AlertSeverity Severity { get; set; } = AlertSeverity.Medium;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Event occurrence threshold within the time window to trigger an alert.
    /// E.g. 5 failed logins within 60 seconds.
    /// </summary>
    public int ThresholdCount { get; set; } = 1;

    /// <summary>
    /// Time window in seconds over which the threshold is evaluated.
    /// </summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Optional regex or comma-separated filters for accounts, objects, or errors.
    /// E.g. "sa,sysadmin,admin" for privileged account rule.
    /// </summary>
    public string? FilterPattern { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
