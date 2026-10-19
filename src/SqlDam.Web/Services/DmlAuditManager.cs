using Microsoft.Data.SqlClient;

namespace SqlDam.Web.Services;

/// <summary>
/// Creates and reconciles a per-database SQL Server Audit Specification that
/// tracks INSERT/UPDATE/DELETE (and TRUNCATE, which SQL Server audits under the
/// DELETE action) on exactly the tables the user has selected for a given
/// database. Attaches to the same shared <see cref="SqlAuditManager.AuditName"/>
/// server audit used for permission/principal events, so there is only ever one
/// audit file target to manage per instance.
///
/// Verified live against a real instance: action_id 'IN'/'UP'/'DL' with
/// class_type 'U' (user table) are produced for INSERT/UPDATE/DELETE and
/// TRUNCATE TABLE; TRUNCATE is distinguishable only by inspecting the captured
/// statement text, since it shares action_id 'DL' with DELETE. Changing a
/// database audit specification's object list requires disabling it first
/// (<c>ALTER DATABASE AUDIT SPECIFICATION ... WITH (STATE = OFF)</c>), then
/// ADD/DROP, then re-enabling -- confirmed by a live ADD-while-enabled attempt
/// failing with "Changes to an audit specification must be done while the
/// audit specification is disabled."
/// </summary>
public sealed class DmlAuditManager
{
    public const string SpecificationName = "SqlDam_DmlSpec";

    private readonly SqlAuditManager _auditManager;
    private readonly ILogger<DmlAuditManager> _logger;

    public DmlAuditManager(SqlAuditManager auditManager, ILogger<DmlAuditManager> logger)
    {
        _auditManager = auditManager;
        _logger = logger;
    }

    /// <summary>
    /// Ensures the database audit specification for one database tracks exactly
    /// the given set of (schema, table) pairs -- adding newly selected tables and
    /// dropping deselected ones. Safe to call repeatedly; a no-op once the live
    /// state already matches. <paramref name="connectionStringForDatabase"/> must
    /// target the specific database (InitialCatalog set).
    /// </summary>
    public async Task SyncDatabaseSpecAsync(
        string connectionStringForDatabase,
        HashSet<(string Schema, string Table)> desiredTables,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionStringForDatabase);
        await connection.OpenAsync(cancellationToken);

        if (!await _auditManager.EnsureServerAuditAsync(connection, cancellationToken))
        {
            return;
        }

        var specExists = await SpecExistsAsync(connection, cancellationToken);

        if (!specExists)
        {
            if (desiredTables.Count == 0)
            {
                // Nothing selected for this database and no spec exists yet -- nothing to do.
                return;
            }

            await using var createCommand = connection.CreateCommand();
            createCommand.CommandTimeout = 10;
            var addClauses = string.Join(",\n    ", desiredTables.Select(FormatAddClause));
            createCommand.CommandText = $"""
                CREATE DATABASE AUDIT SPECIFICATION [{SpecificationName}]
                FOR SERVER AUDIT [{SqlAuditManager.AuditName}]
                {addClauses}
                WITH (STATE = ON);
                """;
            try
            {
                await createCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException ex)
            {
                _logger.LogWarning(ex, "Could not create database audit specification {SpecName}; table data-change events will not be collected for this database.", SpecificationName);
            }

            return;
        }

        var currentTables = await GetCurrentTablesAsync(connection, cancellationToken);
        if (currentTables.SetEquals(desiredTables))
        {
            // Already matches the desired state. If there is at least one tracked
            // table, make sure the spec is left enabled; if the desired set is
            // empty (the user deselected everything), make sure it's left disabled
            // rather than force-enabling a spec with zero actions on every poll.
            var shouldBeEnabled = desiredTables.Count > 0;
            var isEnabled = await IsEnabledAsync(connection, cancellationToken);
            if (shouldBeEnabled != isEnabled)
            {
                await SetStateAsync(connection, enabled: shouldBeEnabled, cancellationToken);
            }

            return;
        }

        var toAdd = desiredTables.Except(currentTables).ToList();
        var toRemove = currentTables.Except(desiredTables).ToList();

        await SetStateAsync(connection, enabled: false, cancellationToken);

        try
        {
            if (toRemove.Count > 0)
            {
                await using var dropCommand = connection.CreateCommand();
                dropCommand.CommandTimeout = 10;
                var dropClauses = string.Join(",\n    ", toRemove.Select(FormatDropClause));
                dropCommand.CommandText = $"ALTER DATABASE AUDIT SPECIFICATION [{SpecificationName}]\n{dropClauses};";
                await dropCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            if (toAdd.Count > 0)
            {
                await using var addCommand = connection.CreateCommand();
                addCommand.CommandTimeout = 10;
                var addClauses = string.Join(",\n    ", toAdd.Select(FormatAddClause));
                addCommand.CommandText = $"ALTER DATABASE AUDIT SPECIFICATION [{SpecificationName}]\n{addClauses};";
                await addCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            // Only re-enable if there is still at least one tracked table; an audit
            // specification with zero actions is left disabled rather than erroring.
            var remaining = desiredTables.Count > 0;
            if (remaining)
            {
                await SetStateAsync(connection, enabled: true, cancellationToken);
            }
        }
    }

    private static string FormatAddClause((string Schema, string Table) t) =>
        $"ADD (INSERT, UPDATE, DELETE ON [{t.Schema}].[{t.Table}] BY public)";

    private static string FormatDropClause((string Schema, string Table) t) =>
        $"DROP (INSERT, UPDATE, DELETE ON [{t.Schema}].[{t.Table}] BY public)";

    private static async Task<bool> SpecExistsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT COUNT(*) FROM sys.database_audit_specifications WHERE name = @name";
        command.Parameters.AddWithValue("@name", SpecificationName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<bool> IsEnabledAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT COUNT(*) FROM sys.database_audit_specifications WHERE name = @name AND is_state_enabled = 1";
        command.Parameters.AddWithValue("@name", SpecificationName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task SetStateAsync(SqlConnection connection, bool enabled, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = $"ALTER DATABASE AUDIT SPECIFICATION [{SpecificationName}] WITH (STATE = {(enabled ? "ON" : "OFF")});";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the live set of (schema, table) pairs the specification currently
    /// covers, by grouping its per-action detail rows back to distinct objects
    /// (each tracked table contributes one row per action: INSERT, UPDATE, DELETE).
    /// </summary>
    private static async Task<HashSet<(string Schema, string Table)>> GetCurrentTablesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            SELECT DISTINCT sch.name AS schema_name, o.name AS table_name
            FROM sys.database_audit_specifications s
            JOIN sys.database_audit_specification_details d ON d.database_specification_id = s.database_specification_id
            JOIN sys.objects o ON o.object_id = d.major_id
            JOIN sys.schemas sch ON sch.schema_id = o.schema_id
            WHERE s.name = @name
            """;
        command.Parameters.AddWithValue("@name", SpecificationName);

        var results = new HashSet<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add((reader.GetString(0), reader.GetString(1)));
        }

        return results;
    }
}
