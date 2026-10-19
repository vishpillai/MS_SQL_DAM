using Microsoft.Data.SqlClient;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

/// <summary>
/// Optional SQL Server store for captured activity and collector cursors. Sources,
/// alerts, rules, and application configuration remain in the local SQLite store.
/// </summary>
public sealed class SqlServerLogStore
{
    private readonly string? _connectionString;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_connectionString);

    public string ProviderName => IsEnabled ? "SQL Server" : "SQLite";

    public SqlServerLogStore(IConfiguration configuration)
    {
        var configuredConnectionString = configuration["LogStore:ConnectionString"];
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return;
        }

        var builder = new SqlConnectionStringBuilder(configuredConnectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog) && string.IsNullOrWhiteSpace(builder.AttachDBFilename))
        {
            throw new InvalidOperationException("LogStore:ConnectionString must specify an existing SQL Server database with Initial Catalog or Database.");
        }

        // Keep the caller's authentication and TLS settings, but do not allow a
        // configured event store to silently disable transport encryption.
        builder.Encrypt = true;
        _connectionString = builder.ConnectionString;
        InitializeSchema();
    }

    public SqlConnection OpenConnection()
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("The SQL Server log store is not configured.");
        }

        var connection = new SqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public (string? FileName, long? Offset) GetCursor(Guid sourceId, string mechanism)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_file_name, last_file_offset FROM dbo.SqlDamSourceCursors WHERE source_id = @sourceId AND mechanism = @mechanism";
        command.Parameters.AddWithValue("@sourceId", sourceId);
        command.Parameters.AddWithValue("@mechanism", mechanism);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (null, null);
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    public void SaveCursor(Guid sourceId, string fileName, long offset, string mechanism)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            MERGE dbo.SqlDamSourceCursors WITH (HOLDLOCK) AS target
            USING (SELECT @sourceId AS source_id, @mechanism AS mechanism) AS source
            ON target.source_id = source.source_id AND target.mechanism = source.mechanism
            WHEN MATCHED THEN UPDATE SET last_file_name = @fileName, last_file_offset = @fileOffset
            WHEN NOT MATCHED THEN INSERT (source_id, mechanism, last_file_name, last_file_offset)
                 VALUES (@sourceId, @mechanism, @fileName, @fileOffset);
            """;
        command.Parameters.AddWithValue("@sourceId", sourceId);
        command.Parameters.AddWithValue("@mechanism", mechanism);
        command.Parameters.AddWithValue("@fileName", fileName);
        command.Parameters.AddWithValue("@fileOffset", offset);
        command.ExecuteNonQuery();
    }

    public void InsertBatch(IReadOnlyCollection<ActivityEvent> events, Guid? defaultTenantId)
    {
        if (events.Count == 0) return;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        InsertBatchInTransaction(connection, transaction, events, defaultTenantId);
        transaction.Commit();
    }

    public void PersistBatchAndCursors(
        IReadOnlyCollection<ActivityEvent> events,
        Guid defaultTenantId,
        Guid sourceId,
        IReadOnlyCollection<(string Mechanism, string FileName, long Offset)> cursors)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        InsertBatchInTransaction(connection, transaction, events, defaultTenantId);
        foreach (var cursor in cursors)
        {
            SaveCursorInTransaction(connection, transaction, sourceId, cursor);
        }
        transaction.Commit();
    }

    private static void InsertBatchInTransaction(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyCollection<ActivityEvent> events,
        Guid? defaultTenantId)
    {
        if (events.Count == 0) return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.SqlDamActivityEvents
                (tenant_id, source_id, source_name, event_name, category, timestamp_utc,
                 login_name, client_host_name, client_app_name, database_name, object_name,
                 summary, detail_xml)
            VALUES
                (@tenantId, @sourceId, @sourceName, @eventName, @category, @timestamp,
                 @login, @clientHost, @clientApp, @database, @object, @summary, @detail)
            """;

        var tenantId = command.Parameters.Add("@tenantId", System.Data.SqlDbType.UniqueIdentifier);
        var sourceId = command.Parameters.Add("@sourceId", System.Data.SqlDbType.UniqueIdentifier);
        var sourceName = command.Parameters.Add("@sourceName", System.Data.SqlDbType.NVarChar, 200);
        var eventName = command.Parameters.Add("@eventName", System.Data.SqlDbType.NVarChar, 100);
        var category = command.Parameters.Add("@category", System.Data.SqlDbType.NVarChar, 40);
        var timestamp = command.Parameters.Add("@timestamp", System.Data.SqlDbType.DateTime2);
        var login = command.Parameters.Add("@login", System.Data.SqlDbType.NVarChar, 256);
        var clientHost = command.Parameters.Add("@clientHost", System.Data.SqlDbType.NVarChar, 256);
        var clientApp = command.Parameters.Add("@clientApp", System.Data.SqlDbType.NVarChar, 512);
        var database = command.Parameters.Add("@database", System.Data.SqlDbType.NVarChar, 128);
        var objectName = command.Parameters.Add("@object", System.Data.SqlDbType.NVarChar, 512);
        var summary = command.Parameters.Add("@summary", System.Data.SqlDbType.NVarChar, 2000);
        var detail = command.Parameters.Add("@detail", System.Data.SqlDbType.NVarChar, -1);

        foreach (var evt in events)
        {
            tenantId.Value = evt.TenantId != Guid.Empty ? evt.TenantId : (defaultTenantId ?? TenantConstants.DefaultTenantId);
            sourceId.Value = evt.SourceId;
            sourceName.Value = Limit(evt.SourceName, 200);
            eventName.Value = Limit(evt.EventName, 100);
            category.Value = evt.Category.ToString();
            timestamp.Value = DateTime.SpecifyKind(evt.TimestampUtc, DateTimeKind.Utc);
            login.Value = NullableLimit(evt.LoginName, 256);
            clientHost.Value = NullableLimit(evt.ClientHostName, 256);
            clientApp.Value = NullableLimit(evt.ClientAppName, 512);
            database.Value = NullableLimit(evt.DatabaseName, 128);
            objectName.Value = NullableLimit(evt.ObjectName, 512);
            summary.Value = Limit(evt.Summary, 2000);
            detail.Value = evt.DetailXml;
            command.ExecuteNonQuery();
        }
    }

    private static void SaveCursorInTransaction(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid sourceId,
        (string Mechanism, string FileName, long Offset) cursor)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            MERGE dbo.SqlDamSourceCursors WITH (HOLDLOCK) AS target
            USING (SELECT @sourceId AS source_id, @mechanism AS mechanism) AS source
            ON target.source_id = source.source_id AND target.mechanism = source.mechanism
            WHEN MATCHED THEN UPDATE SET last_file_name = @fileName, last_file_offset = @fileOffset
            WHEN NOT MATCHED THEN INSERT (source_id, mechanism, last_file_name, last_file_offset)
                 VALUES (@sourceId, @mechanism, @fileName, @fileOffset);
            """;
        command.Parameters.AddWithValue("@sourceId", sourceId);
        command.Parameters.AddWithValue("@mechanism", cursor.Mechanism);
        command.Parameters.AddWithValue("@fileName", cursor.FileName);
        command.Parameters.AddWithValue("@fileOffset", cursor.Offset);
        command.ExecuteNonQuery();

        transaction.Commit();
    }

    public List<ActivityEvent> GetRecent(int take, Guid? sourceId, Guid? tenantId, ActivityEventFilter? filter)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (@take) id, tenant_id, source_id, source_name, event_name, category, timestamp_utc,
                   login_name, client_host_name, client_app_name, database_name, object_name, summary, detail_xml
            FROM dbo.SqlDamActivityEvents
            WHERE (@sourceId IS NULL OR source_id = @sourceId)
              AND (@tenantId IS NULL OR tenant_id = @tenantId)
              AND (@category IS NULL OR category = @category)
              AND (@userName IS NULL OR login_name LIKE @userName)
              AND (@databaseName IS NULL OR database_name LIKE @databaseName)
              AND (@objectName IS NULL OR object_name LIKE @objectName)
              AND (@accessChangesOnly = 0 OR category IN (N'LoginSuccess', N'LoginFailure', N'PermissionChange', N'PrincipalChange'))
              AND (@fromUtc IS NULL OR timestamp_utc >= @fromUtc)
              AND (@toUtc IS NULL OR timestamp_utc <= @toUtc)
            ORDER BY timestamp_utc DESC, id DESC
            """;
        AddFilterParameters(command, take, sourceId, tenantId, filter);
        return ReadEvents(command);
    }

    public List<ActivityEvent> GetEventsForEvaluation(Guid sourceId, Guid tenantId, DateTime startUtc, DateTime endUtc)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT category, timestamp_utc, login_name, client_host_name, summary
            FROM dbo.SqlDamActivityEvents
            WHERE tenant_id = @tenantId AND source_id = @sourceId
              AND timestamp_utc >= @startUtc AND timestamp_utc <= @endUtc
            ORDER BY timestamp_utc DESC
            """;
        command.Parameters.AddWithValue("@tenantId", tenantId);
        command.Parameters.AddWithValue("@sourceId", sourceId);
        command.Parameters.AddWithValue("@startUtc", startUtc);
        command.Parameters.AddWithValue("@endUtc", endUtc);

        var results = new List<ActivityEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ActivityEvent
            {
                SourceId = sourceId,
                TenantId = tenantId,
                Category = Enum.Parse<ActivityCategory>(reader.GetString(0)),
                TimestampUtc = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                LoginName = reader.IsDBNull(2) ? null : reader.GetString(2),
                ClientHostName = reader.IsDBNull(3) ? null : reader.GetString(3),
                Summary = reader.GetString(4)
            });
        }

        return results;
    }

    public int PurgeExpiredEvents(Guid tenantId, int retentionDays)
    {
        if (retentionDays < 1) return 0;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.SqlDamActivityEvents WHERE tenant_id = @tenantId AND timestamp_utc < @cutoff";
        command.Parameters.AddWithValue("@tenantId", tenantId);
        command.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-retentionDays));
        return command.ExecuteNonQuery();
    }

    public int CountSince(DateTime sinceUtc, Guid? tenantId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.SqlDamActivityEvents WHERE timestamp_utc >= @since AND (@tenantId IS NULL OR tenant_id = @tenantId)";
        command.Parameters.AddWithValue("@since", sinceUtc);
        command.Parameters.AddWithValue("@tenantId", (object?)tenantId ?? DBNull.Value);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void InitializeSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'dbo.SqlDamActivityEvents', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.SqlDamActivityEvents (
                    id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SqlDamActivityEvents PRIMARY KEY,
                    tenant_id uniqueidentifier NOT NULL,
                    source_id uniqueidentifier NOT NULL,
                    source_name nvarchar(200) NOT NULL,
                    event_name nvarchar(100) NOT NULL,
                    category nvarchar(40) NOT NULL,
                    timestamp_utc datetime2(7) NOT NULL,
                    login_name nvarchar(256) NULL,
                    client_host_name nvarchar(256) NULL,
                    client_app_name nvarchar(512) NULL,
                    database_name nvarchar(128) NULL,
                    object_name nvarchar(512) NULL,
                    summary nvarchar(2000) NOT NULL,
                    detail_xml nvarchar(max) NOT NULL
                );
            END;
            IF OBJECT_ID(N'dbo.SqlDamSourceCursors', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.SqlDamSourceCursors (
                    source_id uniqueidentifier NOT NULL,
                    mechanism nvarchar(10) NOT NULL,
                    last_file_name nvarchar(1024) NULL,
                    last_file_offset bigint NULL,
                    CONSTRAINT PK_SqlDamSourceCursors PRIMARY KEY (source_id, mechanism)
                );
            END;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SqlDamActivityEvents') AND name = N'IX_SqlDamActivityEvents_TenantTime')
                CREATE INDEX IX_SqlDamActivityEvents_TenantTime ON dbo.SqlDamActivityEvents (tenant_id, timestamp_utc DESC, id DESC);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SqlDamActivityEvents') AND name = N'IX_SqlDamActivityEvents_SourceTime')
                CREATE INDEX IX_SqlDamActivityEvents_SourceTime ON dbo.SqlDamActivityEvents (source_id, timestamp_utc DESC, id DESC);
            """;
        command.ExecuteNonQuery();
    }

    private static void AddFilterParameters(SqlCommand command, int take, Guid? sourceId, Guid? tenantId, ActivityEventFilter? filter)
    {
        command.Parameters.AddWithValue("@take", Math.Clamp(take, 1, 2000));
        command.Parameters.Add("@sourceId", System.Data.SqlDbType.UniqueIdentifier).Value = (object?)(filter?.SourceId ?? sourceId) ?? DBNull.Value;
        command.Parameters.Add("@tenantId", System.Data.SqlDbType.UniqueIdentifier).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@category", System.Data.SqlDbType.NVarChar, 40).Value = (object?)filter?.Category?.ToString() ?? DBNull.Value;
        command.Parameters.Add("@userName", System.Data.SqlDbType.NVarChar, 520).Value = LikePattern(filter?.UserName);
        command.Parameters.Add("@databaseName", System.Data.SqlDbType.NVarChar, 260).Value = LikePattern(filter?.DatabaseName);
        command.Parameters.Add("@objectName", System.Data.SqlDbType.NVarChar, 1024).Value = LikePattern(filter?.ObjectName);
        command.Parameters.Add("@accessChangesOnly", System.Data.SqlDbType.Bit).Value = filter?.AccessChangesOnly == true;
        command.Parameters.Add("@fromUtc", System.Data.SqlDbType.DateTime2).Value = (object?)(filter?.FromUtc?.ToUniversalTime()) ?? DBNull.Value;
        command.Parameters.Add("@toUtc", System.Data.SqlDbType.DateTime2).Value = (object?)(filter?.ToUtc?.ToUniversalTime()) ?? DBNull.Value;
    }

    private static List<ActivityEvent> ReadEvents(SqlCommand command)
    {
        var results = new List<ActivityEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ActivityEvent
            {
                Id = reader.GetInt64(0),
                TenantId = reader.GetGuid(1),
                SourceId = reader.GetGuid(2),
                SourceName = reader.GetString(3),
                EventName = reader.GetString(4),
                Category = Enum.Parse<ActivityCategory>(reader.GetString(5)),
                TimestampUtc = DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
                LoginName = reader.IsDBNull(7) ? null : reader.GetString(7),
                ClientHostName = reader.IsDBNull(8) ? null : reader.GetString(8),
                ClientAppName = reader.IsDBNull(9) ? null : reader.GetString(9),
                DatabaseName = reader.IsDBNull(10) ? null : reader.GetString(10),
                ObjectName = reader.IsDBNull(11) ? null : reader.GetString(11),
                Summary = reader.GetString(12),
                DetailXml = reader.GetString(13)
            });
        }

        return results;
    }

    private static object LikePattern(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : $"%{value.Trim()}%";

    private static string Limit(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private static object NullableLimit(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? DBNull.Value : Limit(value, maxLength);
}
