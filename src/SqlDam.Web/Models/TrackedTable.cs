namespace SqlDam.Web.Models;

/// <summary>
/// A specific table on a monitored SQL source that the user has opted in to
/// schema (CREATE/ALTER/DROP) and/or data-change
/// (INSERT/UPDATE/DELETE/TRUNCATE) tracking.
/// </summary>
public sealed class TrackedTable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; } = TenantConstants.DefaultTenantId;

    public Guid SourceId { get; set; }

    public string DatabaseName { get; set; } = string.Empty;

    public string SchemaName { get; set; } = string.Empty;

    public string TableName { get; set; } = string.Empty;

    public bool CaptureDdl { get; set; }

    public bool CaptureDml { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed record TrackedTableSelection(string SchemaName, string TableName, bool CaptureDdl, bool CaptureDml);
