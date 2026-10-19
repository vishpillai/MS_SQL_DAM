using Microsoft.Data.SqlClient;

namespace SqlDam.Web.Services;

/// <summary>
/// Creates and starts the SQL DAM SQL Server Audit on a monitored instance.
/// SQL Server Audit is the only native mechanism for permission and principal
/// change events on current SQL Server versions — the Extended Events package
/// has no discrete events for GRANT/DENY/REVOKE or login/user/role management
/// (verified directly against <c>sys.dm_xe_objects</c> across every loaded
/// package). The audit is configured so it can never impact the monitored
/// server: <c>ON_FAILURE = CONTINUE</c> means a write failure is swallowed
/// instead of failing the audited statement or shutting the instance down,
/// and <c>QUEUE_DELAY = 1000</c> (the supported minimum above 0, which would
/// mean synchronous/blocking delivery) keeps delivery asynchronous.
/// </summary>
public sealed class SqlAuditManager
{
    public const string AuditName = "SqlDam_SecurityAudit";
    public const string SpecificationName = "SqlDam_SecuritySpec";

    // These groups cover access changes, principal/role changes, server-object
    // DDL, and grants on database/server objects without enabling batch-text audit.
    private static readonly string[] ActionGroups =
    [
        "SCHEMA_OBJECT_PERMISSION_CHANGE_GROUP",
        "DATABASE_PERMISSION_CHANGE_GROUP",
        "DATABASE_OBJECT_PERMISSION_CHANGE_GROUP",
        "SERVER_PERMISSION_CHANGE_GROUP",
        "SERVER_OBJECT_PERMISSION_CHANGE_GROUP",
        "SERVER_OBJECT_CHANGE_GROUP",
        "SERVER_PRINCIPAL_CHANGE_GROUP",
        "DATABASE_PRINCIPAL_CHANGE_GROUP",
        "SERVER_ROLE_MEMBER_CHANGE_GROUP",
        "DATABASE_ROLE_MEMBER_CHANGE_GROUP"
    ];

    private readonly ILogger<SqlAuditManager> _logger;

    public SqlAuditManager(ILogger<SqlAuditManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Ensures the SQL DAM server audit and audit specification exist and are both
    /// enabled. Safe to call repeatedly: every step checks current state first and
    /// is a no-op when already satisfied.
    /// </summary>
    public async Task EnsureAuditAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await EnsureServerAuditAsync(connection, cancellationToken))
        {
            return;
        }

        if (!await SpecificationExistsAsync(connection, cancellationToken))
        {
            await using var createSpecCommand = connection.CreateCommand();
            createSpecCommand.CommandTimeout = 10;
            var addClauses = string.Join(",\n    ", ActionGroups.Select(group => $"ADD ({group})"));
            createSpecCommand.CommandText = $"""
                CREATE SERVER AUDIT SPECIFICATION [{SpecificationName}]
                FOR SERVER AUDIT [{AuditName}]
                {addClauses}
                WITH (STATE = ON);
                """;
            await createSpecCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            var existingGroups = await GetConfiguredActionGroupsAsync(connection, cancellationToken);
            var missingGroups = ActionGroups.Where(group => !existingGroups.Contains(group)).ToArray();
            var isEnabled = await SpecificationIsEnabledAsync(connection, cancellationToken);

            if (missingGroups.Length > 0)
            {
                if (isEnabled)
                {
                    await using var disableCommand = connection.CreateCommand();
                    disableCommand.CommandTimeout = 10;
                    disableCommand.CommandText = $"ALTER SERVER AUDIT SPECIFICATION [{SpecificationName}] WITH (STATE = OFF);";
                    await disableCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await using var addGroupsCommand = connection.CreateCommand();
                addGroupsCommand.CommandTimeout = 10;
                var addClauses = string.Join(",\n    ", missingGroups.Select(group => $"ADD ({group})"));
                addGroupsCommand.CommandText = $"ALTER SERVER AUDIT SPECIFICATION [{SpecificationName}]\n{addClauses};";
                await addGroupsCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            if (!isEnabled || missingGroups.Length > 0)
            {
                await using var enableCommand = connection.CreateCommand();
                enableCommand.CommandTimeout = 10;
                enableCommand.CommandText = $"ALTER SERVER AUDIT SPECIFICATION [{SpecificationName}] WITH (STATE = ON);";
                await enableCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Ensures the shared SqlDam_SecurityAudit server audit object exists and is
    /// enabled (but does not touch the server-level audit SPECIFICATION, which is
    /// owned by <see cref="EnsureAuditAsync"/> alone). Other mechanisms that attach
    /// their own database-level audit specifications to this same server audit
    /// (e.g. <see cref="DmlAuditManager"/>) call this first. Returns false if the
    /// audit object could not be created (e.g. insufficient permission), in which
    /// case the caller should give up for this poll rather than fail loudly.
    /// </summary>
    public async Task<bool> EnsureServerAuditAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (!await AuditExistsAsync(connection, cancellationToken))
        {
            await using var createAuditCommand = connection.CreateCommand();
            createAuditCommand.CommandTimeout = 10;
            createAuditCommand.CommandText = $$"""
                {{SqlServerLogPath.DeclareLogDirectorySql}}
                DECLARE @sql nvarchar(max) = N'
                CREATE SERVER AUDIT [{{AuditName}}]
                TO FILE (FILEPATH = ''' + @logDir + N''', MAXSIZE = 5 MB, MAX_ROLLOVER_FILES = 4)
                WITH (QUEUE_DELAY = 1000, ON_FAILURE = CONTINUE);';
                EXEC sys.sp_executesql @sql;
                """;
            try
            {
                await createAuditCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException ex)
            {
                _logger.LogWarning(ex, "Could not create SQL Server Audit {AuditName}; permission/principal/data-change events will not be collected for this source.", AuditName);
                return false;
            }
        }

        if (!await AuditIsEnabledAsync(connection, cancellationToken))
        {
            await using var enableAuditCommand = connection.CreateCommand();
            enableAuditCommand.CommandTimeout = 10;
            enableAuditCommand.CommandText = $"ALTER SERVER AUDIT [{AuditName}] WITH (STATE = ON);";
            await enableAuditCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return true;
    }

    private static async Task<bool> AuditExistsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT COUNT(*) FROM sys.server_audits WHERE name = @name";
        command.Parameters.AddWithValue("@name", AuditName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<bool> AuditIsEnabledAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT COUNT(*) FROM sys.server_audits WHERE name = @name AND is_state_enabled = 1";
        command.Parameters.AddWithValue("@name", AuditName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<bool> SpecificationExistsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT COUNT(*) FROM sys.server_audit_specifications WHERE name = @name";
        command.Parameters.AddWithValue("@name", SpecificationName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<bool> SpecificationIsEnabledAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT COUNT(*) FROM sys.server_audit_specifications WHERE name = @name AND is_state_enabled = 1";
        command.Parameters.AddWithValue("@name", SpecificationName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<HashSet<string>> GetConfiguredActionGroupsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            SELECT details.audit_action_name
            FROM sys.server_audit_specifications AS specifications
            JOIN sys.server_audit_specification_details AS details
              ON details.server_specification_id = specifications.server_specification_id
            WHERE specifications.name = @name
            """;
        command.Parameters.AddWithValue("@name", SpecificationName);

        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) groups.Add(reader.GetString(0));
        return groups;
    }
}
