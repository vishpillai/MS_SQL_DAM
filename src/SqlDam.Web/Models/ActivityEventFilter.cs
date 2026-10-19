namespace SqlDam.Web.Models;

/// <summary>Optional filters used by the activity investigation page.</summary>
public sealed class ActivityEventFilter
{
    public Guid? SourceId { get; init; }

    public ActivityCategory? Category { get; init; }

    public string? UserName { get; init; }

    public string? DatabaseName { get; init; }

    public string? ObjectName { get; init; }

    public DateTime? FromUtc { get; init; }

    public DateTime? ToUtc { get; init; }

    public bool AccessChangesOnly { get; init; }
}
