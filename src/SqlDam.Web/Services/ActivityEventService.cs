using Microsoft.Data.Sqlite;
using SqlDam.Web.Data;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

/// <summary>Persists captured activity events and the per-source read cursor (bookmark).</summary>
public sealed class ActivityEventService
{
    private readonly LocalDatabase _database;
    private readonly SqlServerLogStore _logStore;

    public ActivityEventService(LocalDatabase database, SqlServerLogStore logStore)
    {
        _database = database;
        _logStore = logStore;
    }

    public (string? FileName, long? Offset) GetCursor(Guid sourceId, string mechanism = "xe")
    {
        if (_logStore.IsEnabled) return _logStore.GetCursor(sourceId, mechanism);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_file_name, last_file_offset FROM source_cursors WHERE source_id = $id AND mechanism = $mechanism";
        command.Parameters.AddWithValue("$id", sourceId.ToString());
        command.Parameters.AddWithValue("$mechanism", mechanism);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return (null, null);
        }

        var fileName = reader.IsDBNull(0) ? null : reader.GetString(0);
        var offset = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        return (fileName, offset);
    }

    public void SaveCursor(Guid sourceId, string fileName, long offset, string mechanism = "xe")
    {
        if (_logStore.IsEnabled)
        {
            _logStore.SaveCursor(sourceId, fileName, offset, mechanism);
            return;
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO source_cursors (source_id, mechanism, last_file_name, last_file_offset)
            VALUES ($id, $mechanism, $file, $offset)
            ON CONFLICT(source_id, mechanism) DO UPDATE SET last_file_name = $file, last_file_offset = $offset
            """;
        command.Parameters.AddWithValue("$id", sourceId.ToString());
        command.Parameters.AddWithValue("$mechanism", mechanism);
        command.Parameters.AddWithValue("$file", fileName);
        command.Parameters.AddWithValue("$offset", offset);
        command.ExecuteNonQuery();
    }

    public void InsertBatch(IReadOnlyCollection<ActivityEvent> events, Guid? defaultTenantId = null)
    {
        if (events.Count == 0)
        {
            return;
        }

        if (_logStore.IsEnabled)
        {
            _logStore.InsertBatch(events, defaultTenantId);
            return;
        }

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO events
                (tenant_id, source_id, source_name, event_name, category, timestamp_utc,
                 login_name, client_host_name, client_app_name, database_name, object_name,
                 summary, detail_xml)
            VALUES
                ($tenantId, $sourceId, $sourceName, $eventName, $category, $timestamp,
                 $login, $clientHost, $clientApp, $database, $object,
                 $summary, $detail)
            """;

        var tenantIdParam = command.CreateParameter(); tenantIdParam.ParameterName = "$tenantId"; command.Parameters.Add(tenantIdParam);
        var sourceId = command.CreateParameter(); sourceId.ParameterName = "$sourceId"; command.Parameters.Add(sourceId);
        var sourceName = command.CreateParameter(); sourceName.ParameterName = "$sourceName"; command.Parameters.Add(sourceName);
        var eventName = command.CreateParameter(); eventName.ParameterName = "$eventName"; command.Parameters.Add(eventName);
        var category = command.CreateParameter(); category.ParameterName = "$category"; command.Parameters.Add(category);
        var timestamp = command.CreateParameter(); timestamp.ParameterName = "$timestamp"; command.Parameters.Add(timestamp);
        var login = command.CreateParameter(); login.ParameterName = "$login"; command.Parameters.Add(login);
        var clientHost = command.CreateParameter(); clientHost.ParameterName = "$clientHost"; command.Parameters.Add(clientHost);
        var clientApp = command.CreateParameter(); clientApp.ParameterName = "$clientApp"; command.Parameters.Add(clientApp);
        var database = command.CreateParameter(); database.ParameterName = "$database"; command.Parameters.Add(database);
        var objectName = command.CreateParameter(); objectName.ParameterName = "$object"; command.Parameters.Add(objectName);
        var summary = command.CreateParameter(); summary.ParameterName = "$summary"; command.Parameters.Add(summary);
        var detail = command.CreateParameter(); detail.ParameterName = "$detail"; command.Parameters.Add(detail);

        foreach (var evt in events)
        {
            var tid = evt.TenantId != Guid.Empty ? evt.TenantId : (defaultTenantId ?? TenantConstants.DefaultTenantId);
            tenantIdParam.Value = tid.ToString();
            sourceId.Value = evt.SourceId.ToString();
            sourceName.Value = evt.SourceName;
            eventName.Value = evt.EventName;
            category.Value = evt.Category.ToString();
            timestamp.Value = evt.TimestampUtc.ToString("O");
            login.Value = (object?)evt.LoginName ?? DBNull.Value;
            clientHost.Value = (object?)evt.ClientHostName ?? DBNull.Value;
            clientApp.Value = (object?)evt.ClientAppName ?? DBNull.Value;
            database.Value = (object?)evt.DatabaseName ?? DBNull.Value;
            objectName.Value = (object?)evt.ObjectName ?? DBNull.Value;
            summary.Value = evt.Summary;
            detail.Value = evt.DetailXml;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public List<ActivityEvent> GetRecent(int take = 200, Guid? sourceId = null, Guid? tenantId = null, ActivityEventFilter? filter = null)
    {
        if (_logStore.IsEnabled) return _logStore.GetRecent(take, sourceId, tenantId, filter);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, tenant_id, source_id, source_name, event_name, category, timestamp_utc,
                   login_name, client_host_name, client_app_name, database_name, object_name,
                   summary, detail_xml
            FROM events
            WHERE ($sourceId IS NULL OR source_id = $sourceId)
              AND ($tenantId IS NULL OR tenant_id = $tenantId)
              AND ($category IS NULL OR category = $category)
              AND ($userName IS NULL OR login_name LIKE $userName)
              AND ($databaseName IS NULL OR database_name LIKE $databaseName)
              AND ($objectName IS NULL OR object_name LIKE $objectName)
              AND ($accessChangesOnly = 0 OR category IN ('LoginSuccess', 'LoginFailure', 'PermissionChange', 'PrincipalChange'))
              AND ($fromUtc IS NULL OR timestamp_utc >= $fromUtc)
              AND ($toUtc IS NULL OR timestamp_utc <= $toUtc)
            ORDER BY timestamp_utc DESC
            LIMIT $take
            """;
        command.Parameters.AddWithValue("$sourceId", (object?)(filter?.SourceId ?? sourceId)?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$tenantId", (object?)tenantId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)filter?.Category?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$userName", LikePattern(filter?.UserName));
        command.Parameters.AddWithValue("$databaseName", LikePattern(filter?.DatabaseName));
        command.Parameters.AddWithValue("$objectName", LikePattern(filter?.ObjectName));
        command.Parameters.AddWithValue("$accessChangesOnly", filter?.AccessChangesOnly == true ? 1 : 0);
        command.Parameters.AddWithValue("$fromUtc", (object?)filter?.FromUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$toUtc", (object?)filter?.ToUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 2000));

        var results = new List<ActivityEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ActivityEvent
            {
                Id = reader.GetInt64(0),
                TenantId = Guid.Parse(reader.GetString(1)),
                SourceId = Guid.Parse(reader.GetString(2)),
                SourceName = reader.GetString(3),
                EventName = reader.GetString(4),
                Category = Enum.Parse<ActivityCategory>(reader.GetString(5)),
                TimestampUtc = DateTime.Parse(reader.GetString(6)).ToUniversalTime(),
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

    /// <summary>
    /// High-performance query for real-time alert evaluation.
    /// Excludes heavy XML columns and queries directly via the (tenant_id, source_id, timestamp_utc) index.
    /// </summary>
    public List<ActivityEvent> GetEventsForEvaluation(Guid sourceId, Guid tenantId, DateTime startUtc, DateTime endUtc)
    {
        if (_logStore.IsEnabled) return _logStore.GetEventsForEvaluation(sourceId, tenantId, startUtc, endUtc);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT category, timestamp_utc, login_name, client_host_name, summary
            FROM events
            WHERE tenant_id = $tenantId
              AND source_id = $sourceId
              AND timestamp_utc >= $start
              AND timestamp_utc <= $end
            ORDER BY timestamp_utc DESC
            """;
        command.Parameters.AddWithValue("$tenantId", tenantId.ToString());
        command.Parameters.AddWithValue("$sourceId", sourceId.ToString());
        command.Parameters.AddWithValue("$start", startUtc.ToString("O"));
        command.Parameters.AddWithValue("$end", endUtc.ToString("O"));

        var results = new List<ActivityEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ActivityEvent
            {
                SourceId = sourceId,
                TenantId = tenantId,
                Category = Enum.Parse<ActivityCategory>(reader.GetString(0)),
                TimestampUtc = DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                LoginName = reader.IsDBNull(2) ? null : reader.GetString(2),
                ClientHostName = reader.IsDBNull(3) ? null : reader.GetString(3),
                Summary = reader.GetString(4)
            });
        }

        return results;
    }

    /// <summary>
    /// Purges events that exceed the configured data retention days for the tenant.
    /// Keeps local SQLite storage bounded and maintains index performance.
    /// </summary>
    public int PurgeExpiredEvents(Guid tenantId, int retentionDays)
    {
        if (retentionDays < 1) return 0;
        if (_logStore.IsEnabled) return _logStore.PurgeExpiredEvents(tenantId, retentionDays);

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("O");

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM events
            WHERE tenant_id = $tenantId AND timestamp_utc < $cutoff;
            """;
        command.Parameters.AddWithValue("$tenantId", tenantId.ToString());
        command.Parameters.AddWithValue("$cutoff", cutoff);
        return command.ExecuteNonQuery();
    }

    public int CountSince(DateTime sinceUtc, Guid? tenantId = null)
    {
        if (_logStore.IsEnabled) return _logStore.CountSince(sinceUtc, tenantId);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM events
            WHERE timestamp_utc >= $since
              AND ($tenantId IS NULL OR tenant_id = $tenantId)
            """;
        command.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));
        command.Parameters.AddWithValue("$tenantId", (object?)tenantId?.ToString() ?? DBNull.Value);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static object LikePattern(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : $"%{value.Trim()}%";
}
