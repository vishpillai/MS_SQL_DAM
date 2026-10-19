using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

/// <summary>Applies SQL DAM table choices to normalized events sent by host agents.</summary>
public static class AgentActivityFilter
{
    public static List<AgentEventItem> Apply(
        IEnumerable<AgentEventItem> events,
        IReadOnlyCollection<TrackedTable> trackedTables)
    {
        return events.Where(item => IsSelected(item.Category, item.ObjectName, item.DatabaseName, trackedTables)).ToList();
    }

    public static List<ActivityEvent> Apply(
        IEnumerable<ActivityEvent> events,
        IReadOnlyCollection<TrackedTable> trackedTables)
    {
        return events.Where(item => IsSelected(item.Category, item.ObjectName, item.DatabaseName, trackedTables)).ToList();
    }

    private static bool IsSelected(
        ActivityCategory category,
        string? objectName,
        string? databaseName,
        IReadOnlyCollection<TrackedTable> trackedTables)
    {
        if (category is not (ActivityCategory.SchemaChange or ActivityCategory.DataChange))
            return true;

        if (string.IsNullOrWhiteSpace(objectName))
        {
            // Keep database/server scope schema events; table DML must identify a table.
            return category == ActivityCategory.SchemaChange;
        }

        var objectParts = objectName
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Replace("\"", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (objectParts.Length == 0) return false;

        var tableName = objectParts[^1];
        var schemaName = objectParts.Length > 1 ? objectParts[^2] : null;
        var enabledMatches = trackedTables.Where(table =>
            (category == ActivityCategory.SchemaChange ? table.CaptureDdl : table.CaptureDml) &&
            string.Equals(table.TableName, tableName, StringComparison.OrdinalIgnoreCase) &&
            (schemaName is null || string.Equals(table.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(databaseName) ||
             string.Equals(table.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase)));

        var matches = enabledMatches.Take(2).Count();
        // Do not guess when the event omits database/schema context and names collide.
        return string.IsNullOrWhiteSpace(databaseName) ? matches == 1 : matches > 0;
    }
}
