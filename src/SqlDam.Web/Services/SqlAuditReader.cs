using System.Data;
using Microsoft.Data.SqlClient;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

/// <summary>
/// Reads new rows from the SQL DAM SQL Server Audit file target and turns them
/// into <see cref="ActivityEvent"/> records. Like <see cref="ExtendedEventsReader"/>,
/// reading is bookmarked by file name/offset so each poll only touches audit
/// data written since the last poll.
///
/// Classification below uses action_id/class_type codes that were verified by
/// creating a live test audit, performing real logins/permission/role actions,
/// and reading the results back with <c>sys.fn_get_audit_file</c> -- they are
/// not guessed from documentation.
/// </summary>
public sealed class SqlAuditReader
{
    public sealed record ReadResult(List<ActivityEvent> Events, string? LastFileName, long? LastFileOffset);

    public async Task<ReadResult> ReadNewEventsAsync(
        SqlConnection connection,
        Guid sourceId,
        string sourceName,
        string? lastFileName,
        long? lastFileOffset,
        CancellationToken cancellationToken,
        int maxRows = 500)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 15;
        command.CommandText = $$"""
            {{SqlServerLogPath.DeclareLogDirectorySql}}
            DECLARE @pattern nvarchar(400) = @logDir + N'{{SqlAuditManager.AuditName}}*.sqlaudit';

            SELECT TOP (@maxRows)
                event_time, action_id, class_type, server_principal_name, database_principal_name,
                target_server_principal_name, target_database_principal_name, database_name,
                schema_name, object_name, statement, succeeded, file_name, audit_file_offset
            FROM sys.fn_get_audit_file(@pattern, @initialFile, @initialOffset)
            WHERE audit_schema_version IS NOT NULL
            ORDER BY file_name, audit_file_offset;
            """;

        command.Parameters.AddWithValue("@initialFile", (object?)lastFileName ?? DBNull.Value);
        command.Parameters.AddWithValue("@initialOffset", (object?)lastFileOffset ?? DBNull.Value);
        command.Parameters.Add("@maxRows", SqlDbType.Int).Value = Math.Clamp(maxRows, 1, 500);

        var events = new List<ActivityEvent>();
        string? newestFileName = lastFileName;
        long? newestFileOffset = lastFileOffset;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fileName = reader.GetString(reader.GetOrdinal("file_name"));
            var offset = reader.GetInt64(reader.GetOrdinal("audit_file_offset"));

            // sys.fn_get_audit_file is inclusive of the bookmark row; skip it so the
            // same event is never processed twice.
            var isBookmarkRow = lastFileName == fileName && lastFileOffset == offset;
            if (!isBookmarkRow)
            {
                var parsed = TryParse(reader, sourceId, sourceName);
                if (parsed is not null)
                {
                    events.Add(parsed);
                }
            }

            newestFileName = fileName;
            newestFileOffset = offset;
        }

        return new ReadResult(events, newestFileName, newestFileOffset);
    }

    private static string? GetNullableString(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        // sys.fn_get_audit_file uses "" (not NULL) for columns that don't apply to a
        // given action_id/class_type (e.g. target_database_principal_name for a
        // server-scoped permission change) -- normalize to null so callers can use
        // plain null-coalescing to fall back to the column that IS populated.
        var value = reader.GetString(ordinal);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static ActivityEvent? TryParse(SqlDataReader reader, Guid sourceId, string sourceName)
    {
        var actionId = GetNullableString(reader, "action_id")?.Trim();
        if (string.IsNullOrEmpty(actionId))
        {
            return null;
        }

        var classType = GetNullableString(reader, "class_type")?.Trim();
        var serverPrincipal = GetNullableString(reader, "server_principal_name");
        var databasePrincipal = GetNullableString(reader, "database_principal_name");
        var targetServerPrincipal = GetNullableString(reader, "target_server_principal_name");
        var targetDatabasePrincipal = GetNullableString(reader, "target_database_principal_name");
        var databaseName = GetNullableString(reader, "database_name");
        var schemaName = GetNullableString(reader, "schema_name");
        var objectName = GetNullableString(reader, "object_name");
        var statement = GetNullableString(reader, "statement");
        var timestampUtc = reader.GetDateTime(reader.GetOrdinal("event_time"));

        var (category, summary, resolvedObjectName) = Classify(
            actionId, classType, serverPrincipal, targetServerPrincipal, targetDatabasePrincipal, databaseName, schemaName, objectName, statement);

        if (category is null)
        {
            // Housekeeping events (e.g. the audit/specification being enabled) are not
            // security-relevant activity for an end user; skip them rather than noise
            // up the feed with an "Other" entry for every internal action_id.
            return null;
        }

        // Data-change (INSERT/UPDATE/DELETE/TRUNCATE) statements can contain literal
        // row values (e.g. an INSERT's VALUES list, an UPDATE's SET/WHERE clause).
        // By design SQL DAM never persists that raw text -- only the safe summary
        // above (who/when/table/action) is stored for these events.
        var isDataChange = category == ActivityCategory.DataChange;

        return new ActivityEvent
        {
            SourceId = sourceId,
            SourceName = sourceName,
            EventName = $"audit:{actionId}",
            Category = category.Value,
            TimestampUtc = DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc),
            LoginName = serverPrincipal,
            ClientHostName = null,
            ClientAppName = null,
            DatabaseName = databaseName,
            ObjectName = resolvedObjectName,
            Summary = summary,
            DetailXml = isDataChange ? string.Empty : statement ?? string.Empty
        };
    }

    private static (ActivityCategory? Category, string Summary, string? ObjectName) Classify(
        string actionId,
        string? classType,
        string? serverPrincipal,
        string? targetServerPrincipal,
        string? targetDatabasePrincipal,
        string? databaseName,
        string? schemaName,
        string? objectName,
        string? statement)
    {
        var actor = serverPrincipal ?? "unknown login";

        switch (actionId)
        {
            // Grant / Deny / Revoke of a permission. class_type identifies the scope:
            // SR = server-level, DB = database-level, U/V = schema object (table/view).
            case "G" or "D" or "R":
                var grantee = targetDatabasePrincipal ?? targetServerPrincipal ?? "unknown principal";
                var verb = actionId switch { "G" => "Granted", "D" => "Denied", _ => "Revoked" };
                var scopeDescription = classType switch
                {
                    "SR" => "server-level permission",
                    "DB" => $"database-level permission in {databaseName ?? "database"}",
                    _ => $"permission on '{objectName ?? "object"}' in {databaseName ?? "database"}"
                };
                return (ActivityCategory.PermissionChange,
                    $"{verb} {scopeDescription} to {grantee} by {actor}",
                    objectName);

            // Create / Alter / Drop a login (class_type SL) or database user (class_type SU).
            case "CR" or "AL" or "DR" when classType is "SL" or "SU":
                var principalKind = classType == "SL" ? "login" : "user";
                var principalVerb = actionId switch { "CR" => "Created", "AL" => "Altered", _ => "Dropped" };
                return (ActivityCategory.PrincipalChange,
                    $"{principalVerb} {principalKind} '{objectName ?? "unknown"}'{(databaseName is null ? string.Empty : $" in {databaseName}")} by {actor}",
                    objectName);

            // Server-scope object changes include database and endpoint CREATE,
            // ALTER, and DROP operations reported by SERVER_OBJECT_CHANGE_GROUP.
            case "CR" or "AL" or "DR":
                var objectVerb = actionId switch { "CR" => "Created", "AL" => "Altered", _ => "Dropped" };
                return (ActivityCategory.SchemaChange,
                    $"{objectVerb} server object '{objectName ?? "unknown"}' by {actor}",
                    objectName);

            // Add / drop a member to a server role (class_type SG) or database role (class_type RL).
            case "APRL" or "DPRL":
                var member = classType == "SG" ? targetServerPrincipal : targetDatabasePrincipal;
                var membershipVerb = actionId == "APRL" ? "Added" : "Removed";
                var roleKind = classType == "SG" ? "server role" : "database role";
                return (ActivityCategory.PrincipalChange,
                    $"{membershipVerb} {member ?? "unknown principal"} {(actionId == "APRL" ? "to" : "from")} {roleKind} '{objectName ?? "unknown"}'{(databaseName is null ? string.Empty : $" in {databaseName}")} by {actor}",
                    objectName);

            // Table data changes (only produced for tables the user explicitly chose
            // to track -- see DmlAuditManager). class_type U = user table.
            // TRUNCATE TABLE is audited under the same action_id as DELETE ('DL');
            // distinguishing it requires inspecting the statement text, since there
            // is no separate action_id for it. The statement itself (which may
            // contain literal row data) is never persisted -- only this safe,
            // value-free summary is.
            case "IN" or "UP" or "DL" when classType == "U":
                var qualifiedName = schemaName is null ? objectName ?? "table" : $"{schemaName}.{objectName ?? "table"}";
                var isTruncate = actionId == "DL" && statement is not null &&
                    statement.TrimStart().StartsWith("TRUNCATE", StringComparison.OrdinalIgnoreCase);
                var dmlVerb = actionId switch
                {
                    "IN" => "Inserted into",
                    "UP" => "Updated",
                    _ when isTruncate => "Truncated",
                    _ => "Deleted from"
                };
                return (ActivityCategory.DataChange,
                    $"{dmlVerb} {qualifiedName}{(databaseName is null ? string.Empty : $" in {databaseName}")} by {actor}",
                    objectName);

            default:
                return (null, string.Empty, null);
        }
    }
}
