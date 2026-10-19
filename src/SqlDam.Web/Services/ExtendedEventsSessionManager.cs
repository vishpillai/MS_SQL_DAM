using Microsoft.Data.SqlClient;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

/// <summary>
/// Creates and starts the SQL DAM Extended Events session on a monitored instance.
/// The session is deliberately configured to never impact the monitored server:
/// it is asynchronous, allowed to drop events under memory pressure rather than
/// block any query, and bounded to a small rolling set of files on disk.
/// </summary>
public sealed class ExtendedEventsSessionManager
{
    public const string SessionName = "SqlDam_SecurityEvents";

    private sealed record EventDefinition(
        string Name,
        string[] Actions,
        string? Predicate = null,
        string[]? SetFields = null);

    // Event names verified directly against sys.dm_xe_objects on a live instance
    // (SQL Server 2025). The older "audit_schema_object_management_event",
    // "audit_schema_object_gdr_event", "audit_server_principal_management_event"
    // and "audit_database_principal_management_event" names used by some
    // documentation/blog examples do not exist as creatable Extended Events on
    // current SQL Server versions; permission and principal changes are captured
    // separately via SQL Server Audit (see <see cref="SqlAuditManager"/>), which
    // is the still-supported native mechanism for those action groups.
    private static readonly EventDefinition[] EventDefinitions =
    [
        new("login", ["client_app_name", "client_hostname", "nt_username", "server_principal_name", "session_id"]),
        new("error_reported", ["client_app_name", "client_hostname", "nt_username", "server_principal_name", "database_name", "session_id"], "([severity] >= (16) OR [error_number] = (18456))"),
        new("object_created", ["client_app_name", "client_hostname", "nt_username", "server_principal_name"], "([ddl_phase] = (1) AND ([database_id] = (1) OR [database_id] > (4)))", ["collect_database_name=(1)"]),
        new("object_altered", ["client_app_name", "client_hostname", "nt_username", "server_principal_name"], "([ddl_phase] = (1) AND ([database_id] = (1) OR [database_id] > (4)))", ["collect_database_name=(1)"]),
        new("object_deleted", ["client_app_name", "client_hostname", "nt_username", "server_principal_name"], "([ddl_phase] = (1) AND ([database_id] = (1) OR [database_id] > (4)))", ["collect_database_name=(1)"])
    ];

    private readonly ILogger<ExtendedEventsSessionManager> _logger;

    public ExtendedEventsSessionManager(ILogger<ExtendedEventsSessionManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// EVENT_RETENTION_MODE=ALLOW_MULTIPLE_EVENT_LOSS and a small MAX_MEMORY buffer mean
    /// the session will silently drop events rather than ever block or slow down a
    /// user query on the monitored instance. MAX_DISPATCH_LATENCY batches writes so the
    /// session does not generate constant small I/O.
    /// </summary>
    /// <summary>
    /// Ensures the SQL DAM Extended Events session exists and is running on the target
    /// instance. Safe to call repeatedly: every step checks current state first and is a
    /// no-op when already satisfied, so it never re-creates or restarts a healthy session.
    /// </summary>
    public async Task EnsureSessionAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var (availableEvents, availableActions) = await GetAvailableObjectsAsync(connection, cancellationToken);
        var supportedDefinitions = EventDefinitions
            .Where(definition => availableEvents.Contains(definition.Name))
            .ToArray();

        if (!supportedDefinitions.Any(definition => definition.Name == "login") ||
            !supportedDefinitions.Any(definition => definition.Name == "error_reported"))
        {
            throw new InvalidOperationException("This SQL Server does not expose the required login and error_reported Extended Events.");
        }

        var missingOptionalEvents = EventDefinitions
            .Where(definition => definition.Name is not "login" and not "error_reported" && !availableEvents.Contains(definition.Name))
            .Select(definition => definition.Name)
            .ToArray();
        if (missingOptionalEvents.Length > 0)
        {
            _logger.LogWarning("SQL Server does not support these optional Extended Events; they will not be collected: {EventNames}", string.Join(", ", missingOptionalEvents));
        }

        var sessionExists = await SessionExistsAsync(connection, cancellationToken);
        if (sessionExists && !await SessionMatchesDesiredEventsAsync(connection, supportedDefinitions, cancellationToken))
        {
            // A session with this name already exists but its event list is out of
            // date (e.g. left over from an older version of this app, or created
            // before the SQL Server instance supported an event we now use). Drop
            // and recreate it rather than silently under-collecting forever.
            _logger.LogInformation("Recreating Extended Events session {SessionName}: its event list is out of date.", SessionName);
            await using var dropCommand = connection.CreateCommand();
            dropCommand.CommandTimeout = 10;
            dropCommand.CommandText = $"DROP EVENT SESSION [{SessionName}] ON SERVER;";
            await dropCommand.ExecuteNonQueryAsync(cancellationToken);
            sessionExists = false;
        }

        if (!sessionExists)
        {
            await using var createCommand = connection.CreateCommand();
            createCommand.CommandTimeout = 10;
            createCommand.CommandText = BuildCreateSessionSql(supportedDefinitions, availableActions);
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await SessionIsRunningAsync(connection, cancellationToken))
        {
            await using var startCommand = connection.CreateCommand();
            startCommand.CommandTimeout = 10;
            startCommand.CommandText = $"ALTER EVENT SESSION [{SessionName}] ON SERVER STATE = START;";
            await startCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<(HashSet<string> Events, HashSet<string> Actions)> GetAvailableObjectsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            SELECT objects.object_type, objects.name
            FROM sys.dm_xe_objects AS objects
            INNER JOIN sys.dm_xe_packages AS packages ON packages.guid = objects.package_guid
            WHERE packages.name = N'sqlserver'
              AND objects.object_type IN (N'event', N'action')
            """;

        var events = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var objectType = reader.GetString(0);
            var name = reader.GetString(1);
            if (objectType == "event")
            {
                events.Add(name);
            }
            else if (objectType == "action")
            {
                actions.Add(name);
            }
        }

        return (events, actions);
    }

    private static string BuildCreateSessionSql(
        IReadOnlyCollection<EventDefinition> definitions,
        HashSet<string> availableActions)
    {
        var eventStatements = definitions.Select(definition =>
        {
            var actions = definition.Actions
                .Where(availableActions.Contains)
                .Select(action => $"sqlserver.{action}")
                .ToArray();

            var clauses = new List<string>();
            if (definition.SetFields is { Length: > 0 })
            {
                clauses.Add($"SET {string.Join(", ", definition.SetFields)}");
            }

            if (actions.Length > 0)
            {
                clauses.Add($"ACTION ({string.Join(", ", actions)})");
            }

            if (definition.Predicate is not null)
            {
                clauses.Add($"WHERE {definition.Predicate}");
            }

            var statement = $"ADD EVENT sqlserver.{definition.Name}";
            if (clauses.Count > 0)
            {
                statement += $"({string.Join(" ", clauses)})";
            }

            return statement;
        });

        return $"""
            CREATE EVENT SESSION [{SessionName}] ON SERVER
            {string.Join(",\n", eventStatements)}
            ADD TARGET package0.event_file(
                SET filename = N'{SessionName}.xel', max_file_size = 5, max_rollover_files = 4
            )
            WITH (
                MAX_MEMORY = 4096 KB,
                EVENT_RETENTION_MODE = ALLOW_MULTIPLE_EVENT_LOSS,
                MAX_DISPATCH_LATENCY = 30 SECONDS,
                MAX_EVENT_SIZE = 0 KB,
                MEMORY_PARTITION_MODE = NONE,
                TRACK_CAUSALITY = OFF,
                STARTUP_STATE = ON
            );
            """;
    }

    private static async Task<bool> SessionExistsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT COUNT(*) FROM sys.server_event_sessions WHERE name = @name";
        command.Parameters.AddWithValue("@name", SessionName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<bool> SessionIsRunningAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT COUNT(*) FROM sys.dm_xe_sessions WHERE name = @name";
        command.Parameters.AddWithValue("@name", SessionName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    /// <summary>
    /// Compares the event list of the already-created session on the server against
    /// the event list this version of the app wants. A mismatch means the session
    /// was created by an older/different version of SQL DAM (or the instance's
    /// supported events changed) and must be recreated, otherwise it would silently
    /// keep missing event types forever.
    /// </summary>
    private static async Task<bool> SessionMatchesDesiredEventsAsync(
        SqlConnection connection,
        IReadOnlyCollection<EventDefinition> desiredDefinitions,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            SELECT events.name, events.predicate
            FROM sys.server_event_sessions AS sessions
            INNER JOIN sys.server_event_session_events AS events ON events.event_session_id = sessions.event_session_id
            WHERE sessions.name = @name
            """;
        command.Parameters.AddWithValue("@name", SessionName);

        var actualEvents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                actualEvents[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        if (actualEvents.Count != desiredDefinitions.Count) return false;
        foreach (var definition in desiredDefinitions)
        {
            if (!actualEvents.TryGetValue(definition.Name, out var actualPredicate) ||
                !string.Equals(NormalizePredicate(actualPredicate), NormalizePredicate(definition.Predicate), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string? NormalizePredicate(string? predicate) =>
        predicate is null ? null : string.Concat(predicate.Where(character => !char.IsWhiteSpace(character)));
}
