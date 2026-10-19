using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

public sealed record DatabaseSummary(string Name);

public sealed record TableSummary(string SchemaName, string TableName);

/// <summary>
/// Lists databases and tables with read-only catalog queries for each supported
/// source engine. User data is never read.
/// </summary>
public sealed class SqlSchemaBrowserService(DatabaseConnectionFactory connectionFactory)
{
    public async Task<List<DatabaseSummary>> ListDatabasesAsync(
        DatabaseEngine engine,
        string connectionString,
        string? configuredDatabase,
        CancellationToken cancellationToken)
    {
        if (engine == DatabaseEngine.Db2)
        {
            // Db2 connects to one catalog database per connection; it cannot
            // enumerate arbitrary remote databases from an ordinary SQL session.
            return string.IsNullOrWhiteSpace(configuredDatabase)
                ? []
                : [new DatabaseSummary(configuredDatabase)];
        }

        await using var connection = connectionFactory.Create(engine, connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = engine switch
        {
            DatabaseEngine.SqlServer => """
                SELECT name FROM sys.databases
                WHERE (database_id = 1 OR database_id > 4) AND state = 0 AND is_read_only = 0
                ORDER BY name
                """,
            DatabaseEngine.PostgreSql => """
                SELECT datname FROM pg_database
                WHERE datallowconn AND NOT datistemplate
                ORDER BY datname
                """,
            DatabaseEngine.MySql => "SHOW DATABASES",
            _ => throw new NotSupportedException($"Database engine '{engine}' is not supported.")
        };

        var results = new List<DatabaseSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DatabaseSummary(reader.GetString(0)));
        }

        return results;
    }

    public async Task<List<TableSummary>> ListTablesAsync(
        DatabaseEngine engine,
        string connectionStringForDatabase,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Create(engine, connectionStringForDatabase);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = engine switch
        {
            DatabaseEngine.SqlServer => """
                SELECT s.name AS schema_name, t.name AS table_name
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE t.is_ms_shipped = 0
                ORDER BY s.name, t.name
                """,
            DatabaseEngine.PostgreSql => """
                SELECT table_schema, table_name
                FROM information_schema.tables
                WHERE table_type = 'BASE TABLE'
                  AND table_schema NOT IN ('pg_catalog', 'information_schema')
                ORDER BY table_schema, table_name
                """,
            DatabaseEngine.MySql => """
                SELECT table_schema, table_name
                FROM information_schema.tables
                WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'
                ORDER BY table_schema, table_name
                """,
            DatabaseEngine.Db2 => """
                SELECT tabschema, tabname
                FROM syscat.tables
                WHERE type = 'T' AND tabschema NOT LIKE 'SYS%'
                ORDER BY tabschema, tabname
                """,
            _ => throw new NotSupportedException($"Database engine '{engine}' is not supported.")
        };

        var results = new List<TableSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TableSummary(reader.GetString(0), reader.GetString(1)));
        }

        return results;
    }
}
