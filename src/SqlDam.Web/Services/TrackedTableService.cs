using Microsoft.Data.Sqlite;
using SqlDam.Web.Data;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

/// <summary>
/// Stores table-level DDL and DML filters. Collection only occurs when the
/// configured read-only audit feed includes those events; these selections do
/// not configure or change server-side auditing.
/// </summary>
public sealed class TrackedTableService
{
    private readonly LocalDatabase _database;
    private readonly AuditService _auditService;

    public TrackedTableService(LocalDatabase database, AuditService auditService)
    {
        _database = database;
        _auditService = auditService;
    }

    /// <summary>All tracked tables for a source, across every database.</summary>
    public List<TrackedTable> GetForSource(Guid sourceId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, tenant_id, source_id, database_name, schema_name, table_name, capture_ddl, capture_dml, created_at_utc
            FROM tracked_tables
            WHERE source_id = $sourceId
            ORDER BY database_name, schema_name, table_name
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId.ToString());

        var results = new List<TrackedTable>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }

        return results;
    }

    /// <summary>Tracked tables for a source, scoped to one database.</summary>
    public HashSet<(string Schema, string Table)> GetTrackedObjectNames(Guid sourceId, string databaseName)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_name, table_name
            FROM tracked_tables
            WHERE source_id = $sourceId AND database_name = $database AND capture_dml = 1
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId.ToString());
        command.Parameters.AddWithValue("$database", databaseName);

        var results = new HashSet<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetString(1)));
        }

        return results;
    }

    public HashSet<(string Schema, string Table)> GetDdlTrackedObjectNames(Guid sourceId, string databaseName)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_name, table_name FROM tracked_tables WHERE source_id = $sourceId AND database_name = $database AND capture_ddl = 1";
        command.Parameters.AddWithValue("$sourceId", sourceId.ToString());
        command.Parameters.AddWithValue("$database", databaseName);

        var results = new HashSet<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) results.Add((reader.GetString(0), reader.GetString(1)));
        return results;
    }

    public Dictionary<(string Schema, string Table), TrackedTableSelection> GetSelections(Guid sourceId, string databaseName)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_name, table_name, capture_ddl, capture_dml FROM tracked_tables WHERE source_id = $sourceId AND database_name = $database";
        command.Parameters.AddWithValue("$sourceId", sourceId.ToString());
        command.Parameters.AddWithValue("$database", databaseName);

        var results = new Dictionary<(string, string), TrackedTableSelection>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            results[(schema, table)] = new TrackedTableSelection(schema, table, reader.GetInt64(2) != 0, reader.GetInt64(3) != 0);
        }
        return results;
    }

    /// <summary>
    /// Every database on a source that currently has, or has ever had, DML
    /// tracking configured -- including ones where all tables have since been
    /// deselected, so the collector still revisits and disables their audit spec.
    /// </summary>
    public List<string> GetDatabasesToReconcile(Guid sourceId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT database_name FROM tracked_table_databases WHERE source_id = $sourceId";
        command.Parameters.AddWithValue("$sourceId", sourceId.ToString());

        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    /// <summary>
    /// Replaces the full set of tracked tables for one (source, database) pair with
    /// exactly the given list, so the UI can simply submit "these are checked now"
    /// and not worry about computing a diff itself.
    /// </summary>
    public void SetTrackedTables(
        Guid tenantId,
        Guid sourceId,
        string databaseName,
        IEnumerable<TrackedTableSelection> selectedTables,
        string actor = "administrator")
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var touchCommand = connection.CreateCommand())
        {
            touchCommand.Transaction = transaction;
            touchCommand.CommandText = "INSERT OR IGNORE INTO tracked_table_databases (source_id, database_name) VALUES ($sourceId, $database)";
            touchCommand.Parameters.AddWithValue("$sourceId", sourceId.ToString());
            touchCommand.Parameters.AddWithValue("$database", databaseName);
            touchCommand.ExecuteNonQuery();
        }

        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM tracked_tables WHERE source_id = $sourceId AND database_name = $database";
            deleteCommand.Parameters.AddWithValue("$sourceId", sourceId.ToString());
            deleteCommand.Parameters.AddWithValue("$database", databaseName);
            deleteCommand.ExecuteNonQuery();
        }

        var insertedCount = 0;
        var ddlCount = 0;
        var dmlCount = 0;
        foreach (var selection in selectedTables.Where(t => t.CaptureDdl || t.CaptureDml))
        {
            using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO tracked_tables (id, tenant_id, source_id, database_name, schema_name, table_name, capture_ddl, capture_dml, created_at_utc)
                VALUES ($id, $tenantId, $sourceId, $database, $schema, $table, $captureDdl, $captureDml, $createdAt)
                """;
            insertCommand.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            insertCommand.Parameters.AddWithValue("$tenantId", tenantId.ToString());
            insertCommand.Parameters.AddWithValue("$sourceId", sourceId.ToString());
            insertCommand.Parameters.AddWithValue("$database", databaseName);
            insertCommand.Parameters.AddWithValue("$schema", selection.SchemaName);
            insertCommand.Parameters.AddWithValue("$table", selection.TableName);
            insertCommand.Parameters.AddWithValue("$captureDdl", selection.CaptureDdl ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$captureDml", selection.CaptureDml ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
            insertCommand.ExecuteNonQuery();
            insertedCount++;
            if (selection.CaptureDdl) ddlCount++;
            if (selection.CaptureDml) dmlCount++;
        }

        transaction.Commit();

        _auditService.Log(
            tenantId,
            actor,
            "TRACKED_TABLES_UPDATED",
            "TrackedTable",
            sourceId.ToString(),
            $"Set table tracking for database '{databaseName}' to {insertedCount} table(s) (DDL: {ddlCount}, DML: {dmlCount}).");
    }

    private static TrackedTable Map(SqliteDataReader reader)
    {
        return new TrackedTable
        {
            Id = Guid.Parse(reader.GetString(0)),
            TenantId = Guid.Parse(reader.GetString(1)),
            SourceId = Guid.Parse(reader.GetString(2)),
            DatabaseName = reader.GetString(3),
            SchemaName = reader.GetString(4),
            TableName = reader.GetString(5),
            CaptureDdl = reader.GetInt64(6) != 0,
            CaptureDml = reader.GetInt64(7) != 0,
            CreatedAtUtc = DateTime.Parse(reader.GetString(8)).ToUniversalTime()
        };
    }
}
