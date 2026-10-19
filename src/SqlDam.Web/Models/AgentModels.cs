namespace SqlDam.Web.Models;

/// <summary>
/// Normalized event payload sent by a customer-managed read-only host agent over HTTPS.
/// </summary>
public sealed class AgentIngestBatch
{
    public Guid SourceId { get; set; }

    public DatabaseEngine Engine { get; set; } = DatabaseEngine.SqlServer;

    public string SourceName { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    public string? DatabaseName { get; set; }

    public string? LastFileName { get; set; }

    public long? LastFileOffset { get; set; }

    public List<AgentEventItem> Events { get; set; } = [];
}

public sealed class AgentEventItem
{
    public string EventName { get; set; } = string.Empty;

    public ActivityCategory Category { get; set; }

    public DateTime TimestampUtc { get; set; }

    public string? LoginName { get; set; }

    public string? ClientHostName { get; set; }

    public string? ClientAppName { get; set; }

    public string? DatabaseName { get; set; }

    public string? ObjectName { get; set; }

    public string Summary { get; set; } = string.Empty;

    public string DetailXml { get; set; } = string.Empty;
}

public sealed class AgentIngestResponse
{
    public bool Success { get; set; }

    public int IngestedCount { get; set; }

    public int AlertsTriggered { get; set; }

    public string? Message { get; set; }
}
