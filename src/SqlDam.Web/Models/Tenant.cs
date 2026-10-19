namespace SqlDam.Web.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Represents an organization. In the on-premises release, an installation is
/// configured for one organization; multi-organization access requires membership checks.
/// </summary>
public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Identifier { get; set; } = string.Empty;

    /// <summary>
    /// Returned only when a key is first generated; never loaded from persistent storage.
    /// </summary>
    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Configured retention period. The required value depends on the deployment's control mapping.</summary>
    public int DataRetentionDays { get; set; } = 180;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastAgentHeartbeatUtc { get; set; }
}
