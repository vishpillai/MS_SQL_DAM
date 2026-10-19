using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using System.Data.Common;
using SqlDam.Web.Data;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

/// <summary>Stores and retrieves monitored database source configurations.</summary>
public sealed class SqlSourceService
{
    private readonly LocalDatabase _database;
    private readonly SourceSecretProtector _protector;
    private readonly AuditService _auditService;
    private readonly DatabaseConnectionFactory _connectionFactory;

    public SqlSourceService(LocalDatabase database, SourceSecretProtector protector, AuditService auditService, DatabaseConnectionFactory connectionFactory)
    {
        _database = database;
        _protector = protector;
        _auditService = auditService;
        _connectionFactory = connectionFactory;
    }

    public List<SqlSource> GetAll(Guid? tenantId = null)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, tenant_id, name, server_name, collection_mode, auth_mode, sql_username, protected_password,
                   enabled, poll_interval_seconds, created_at_utc, last_polled_at_utc,
                   last_status, last_error, trust_server_certificate, database_engine, port, initial_database, tls_certificate_path
            FROM sources
            WHERE ($tenantId IS NULL OR tenant_id = $tenantId)
            ORDER BY created_at_utc DESC
            """;
        command.Parameters.AddWithValue("$tenantId", (object?)tenantId?.ToString() ?? DBNull.Value);

        var sources = new List<SqlSource>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sources.Add(Map(reader));
        }

        return sources;
    }

    public SqlSource? Get(Guid id, Guid? tenantId = null)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, tenant_id, name, server_name, collection_mode, auth_mode, sql_username, protected_password,
                   enabled, poll_interval_seconds, created_at_utc, last_polled_at_utc,
                   last_status, last_error, trust_server_certificate, database_engine, port, initial_database, tls_certificate_path
            FROM sources
            WHERE id = $id AND ($tenantId IS NULL OR tenant_id = $tenantId)
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$tenantId", (object?)tenantId?.ToString() ?? DBNull.Value);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public SqlSource Add(
        Guid tenantId,
        string name,
        string serverName,
        SourceCollectionMode collectionMode,
        SqlAuthMode authMode,
        string? sqlUsername,
        string? plainPassword,
        int pollIntervalSeconds,
        string actor = "administrator",
        Guid? sourceId = null,
        bool trustServerCertificate = false,
        DatabaseEngine engine = DatabaseEngine.SqlServer,
        int port = 0,
        string? initialDatabase = null,
        string? tlsCertificatePath = null)
    {
        var source = new SqlSource
        {
            Id = sourceId ?? Guid.NewGuid(),
            TenantId = tenantId,
            Name = name.Trim(),
            ServerName = serverName.Trim(),
            Engine = engine,
            Port = port,
            InitialDatabase = string.IsNullOrWhiteSpace(initialDatabase) ? null : initialDatabase.Trim(),
            TlsCertificatePath = string.IsNullOrWhiteSpace(tlsCertificatePath) ? null : tlsCertificatePath.Trim(),
            TrustServerCertificate = trustServerCertificate,
            CollectionMode = collectionMode,
            AuthMode = authMode,
            SqlUsername = authMode == SqlAuthMode.SqlLogin ? sqlUsername?.Trim() : null,
            ProtectedPassword = authMode == SqlAuthMode.SqlLogin && !string.IsNullOrEmpty(plainPassword)
                ? _protector.Protect(plainPassword)
                : null,
            PollIntervalSeconds = Math.Clamp(pollIntervalSeconds, 30, 3600)
        };

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sources
                (id, tenant_id, name, server_name, collection_mode, auth_mode, sql_username, protected_password,
                 enabled, poll_interval_seconds, created_at_utc, last_polled_at_utc, last_status, last_error,
                 trust_server_certificate, database_engine, port, initial_database, tls_certificate_path)
            VALUES
                ($id, $tenantId, $name, $server, $colMode, $auth, $user, $password,
                 $enabled, $interval, $created, NULL, $status, NULL, $trustServerCertificate, $engine, $port, $initialDatabase, $tlsCertificatePath)
            """;
        command.Parameters.AddWithValue("$id", source.Id.ToString());
        command.Parameters.AddWithValue("$tenantId", source.TenantId.ToString());
        command.Parameters.AddWithValue("$name", source.Name);
        command.Parameters.AddWithValue("$server", source.ServerName);
        command.Parameters.AddWithValue("$colMode", source.CollectionMode.ToString());
        command.Parameters.AddWithValue("$auth", source.AuthMode.ToString());
        command.Parameters.AddWithValue("$user", (object?)source.SqlUsername ?? DBNull.Value);
        command.Parameters.AddWithValue("$password", (object?)source.ProtectedPassword ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", source.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$interval", source.PollIntervalSeconds);
        command.Parameters.AddWithValue("$created", source.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$status", source.LastStatus);
        command.Parameters.AddWithValue("$trustServerCertificate", source.TrustServerCertificate ? 1 : 0);
        command.Parameters.AddWithValue("$engine", source.Engine.ToString());
        command.Parameters.AddWithValue("$port", source.Port);
        command.Parameters.AddWithValue("$initialDatabase", (object?)source.InitialDatabase ?? DBNull.Value);
        command.Parameters.AddWithValue("$tlsCertificatePath", (object?)source.TlsCertificatePath ?? DBNull.Value);
        command.ExecuteNonQuery();

        _auditService.Log(
            source.TenantId,
            actor,
            "SOURCE_ADDED",
            "SqlSource",
            source.Id.ToString(),
            $"Added monitored source '{source.Name}' ({source.ServerName}, Mode: {source.CollectionMode})");

        return source;
    }

    public SqlSource Add(string name, string serverName, SqlAuthMode authMode, string? sqlUsername, string? plainPassword, int pollIntervalSeconds)
    {
        return Add(TenantConstants.DefaultTenantId, name, serverName, SourceCollectionMode.InProcessCollector, authMode, sqlUsername, plainPassword, pollIntervalSeconds);
    }

    public void SetEnabled(Guid id, bool enabled, string actor = "administrator", Guid? tenantId = null)
    {
        var source = Get(id, tenantId);
        if (source is null) return;

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sources SET enabled = $enabled WHERE id = $id AND ($tenantId IS NULL OR tenant_id = $tenantId)";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$tenantId", (object?)tenantId?.ToString() ?? DBNull.Value);
        command.ExecuteNonQuery();

        _auditService.Log(
            source.TenantId,
            actor,
            enabled ? "SOURCE_ENABLED" : "SOURCE_DISABLED",
            "SqlSource",
            id.ToString(),
            $"Monitored source '{source.Name}' set to {(enabled ? "enabled" : "disabled")}");
    }

    public void Delete(Guid id, string actor = "administrator", Guid? tenantId = null)
    {
        var source = Get(id, tenantId);
        if (source is null) return;

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sources WHERE id = $id AND ($tenantId IS NULL OR tenant_id = $tenantId)";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$tenantId", (object?)tenantId?.ToString() ?? DBNull.Value);
        command.ExecuteNonQuery();

        _auditService.Log(
            source.TenantId,
            actor,
            "SOURCE_DELETED",
            "SqlSource",
            id.ToString(),
            $"Deleted monitored source '{source.Name}'");
    }

    public async Task ValidateConnectionAsync(
        string serverName,
        SqlAuthMode authMode,
        string? sqlUsername,
        string? plainPassword,
        CancellationToken cancellationToken = default,
        bool trustServerCertificate = false,
        DatabaseEngine engine = DatabaseEngine.SqlServer,
        int port = 0,
        string? initialDatabase = null,
        string? tlsCertificatePath = null)
    {
        var source = new SqlSource
        {
            ServerName = serverName.Trim(),
            Engine = engine,
            Port = port,
            InitialDatabase = string.IsNullOrWhiteSpace(initialDatabase) ? null : initialDatabase.Trim(),
            TlsCertificatePath = string.IsNullOrWhiteSpace(tlsCertificatePath) ? null : tlsCertificatePath.Trim(),
            AuthMode = authMode,
            TrustServerCertificate = trustServerCertificate,
            SqlUsername = authMode == SqlAuthMode.SqlLogin ? sqlUsername?.Trim() : null
        };

        ValidateSourceSettings(source);
        if (authMode == SqlAuthMode.SqlLogin &&
            (string.IsNullOrWhiteSpace(source.SqlUsername) || string.IsNullOrEmpty(plainPassword)))
            throw new InvalidOperationException("A username and password are required.");
        ValidateCertificatePath(source);
        var connectionString = BuildConnectionString(
            source,
            database: null,
            plainPassword: plainPassword,
            passwordIsPlainText: true);
        await using var connection = _connectionFactory.Create(engine, connectionString);
        await connection.OpenAsync(cancellationToken);
    }

    public async Task UpdateServerEndpointAsync(
        Guid id,
        Guid tenantId,
        string serverName,
        string actor,
        CancellationToken cancellationToken = default,
        bool trustServerCertificate = false,
        int? port = null,
        string? tlsCertificatePath = null)
    {
        var source = Get(id, tenantId) ?? throw new InvalidOperationException("The SQL source was not found.");
        var newServerName = serverName.Trim();
        var newPort = port ?? source.Port;
        var newTlsCertificatePath = tlsCertificatePath ?? source.TlsCertificatePath;
        var plainPassword = source.AuthMode == SqlAuthMode.SqlLogin && !string.IsNullOrEmpty(source.ProtectedPassword)
            ? _protector.Unprotect(source.ProtectedPassword)
            : null;

        await ValidateConnectionAsync(
            newServerName,
            source.AuthMode,
            source.SqlUsername,
            plainPassword,
            cancellationToken,
            trustServerCertificate,
            source.Engine,
            newPort,
            source.InitialDatabase,
            newTlsCertificatePath);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sources SET server_name = $server, port = $port, trust_server_certificate = $trustServerCertificate, tls_certificate_path = $tlsCertificatePath, last_status = $status, last_error = NULL WHERE id = $id AND tenant_id = $tenantId";
        command.Parameters.AddWithValue("$server", newServerName);
        command.Parameters.AddWithValue("$port", newPort);
        command.Parameters.AddWithValue("$trustServerCertificate", trustServerCertificate ? 1 : 0);
        command.Parameters.AddWithValue("$tlsCertificatePath", (object?)newTlsCertificatePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", source.Enabled ? "Endpoint verified; waiting for next poll" : "Disabled");
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$tenantId", tenantId.ToString());
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The SQL source was not found.");
        }

        _auditService.Log(
            tenantId,
            actor,
            "SOURCE_ENDPOINT_UPDATED",
            "SqlSource",
            id.ToString(),
            $"Updated connection endpoint for monitored source '{source.Name}'");
    }

    public void UpdatePollResult(Guid id, bool success, string status, string? error)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sources
            SET last_polled_at_utc = $polled, last_status = $status, last_error = $error
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$polled", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Builds a connection string tuned for a lightweight, short-lived monitoring
    /// connection: short timeouts and a small dedicated pool so the collector never
    /// competes with application traffic on the target instance.
    /// </summary>
    public DbConnection CreateConnection(SqlSource source, string? database = null) =>
        _connectionFactory.Create(source.Engine, BuildConnectionString(source, database));

    public string BuildConnectionString(SqlSource source) => BuildConnectionString(source, database: null);

    /// <summary>
    /// Builds a connection string targeting a specific database, used for operations
    /// that must run in a particular database's context (browsing its tables,
    /// creating a per-database audit specification for tracked-table DML).
    /// </summary>
    public string BuildConnectionString(SqlSource source, string? database) =>
        BuildConnectionString(source, database, plainPassword: null, passwordIsPlainText: false);

    private string BuildConnectionString(SqlSource source, string? database, string? plainPassword, bool passwordIsPlainText)
    {
        var effectiveDatabase = string.IsNullOrWhiteSpace(database) ? source.InitialDatabase : database;
        var password = passwordIsPlainText
            ? plainPassword ?? string.Empty
            : string.IsNullOrEmpty(source.ProtectedPassword)
                ? string.Empty
                : _protector.Unprotect(source.ProtectedPassword);

        if (source.Engine == DatabaseEngine.SqlServer)
        {
            if (source.AuthMode == SqlAuthMode.WindowsIntegrated && !OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("Windows Integrated authentication is available only when SQL DAM runs on Windows. Use a SQL Server login on Linux.");
            }

            var sqlBuilder = new SqlConnectionStringBuilder
            {
                DataSource = source.ServerName,
                ApplicationName = "SQL DAM Collector",
                ConnectTimeout = 15,
                Encrypt = true,
                TrustServerCertificate = source.TrustServerCertificate,
                Pooling = true,
                MaxPoolSize = 2,
                MinPoolSize = 0
            };

            if (!string.IsNullOrWhiteSpace(effectiveDatabase)) sqlBuilder.InitialCatalog = effectiveDatabase;
            if (source.AuthMode == SqlAuthMode.SqlLogin)
            {
                sqlBuilder.UserID = source.SqlUsername ?? string.Empty;
                sqlBuilder.Password = password;
            }
            else
            {
                sqlBuilder.IntegratedSecurity = true;
            }

            return sqlBuilder.ConnectionString;
        }

        if (source.AuthMode != SqlAuthMode.SqlLogin)
        {
            throw new InvalidOperationException($"Windows Integrated authentication is only supported for Microsoft SQL Server sources.");
        }

        var builder = new DbConnectionStringBuilder();
        switch (source.Engine)
        {
            case DatabaseEngine.PostgreSql:
                builder["Host"] = source.ServerName;
                builder["Port"] = source.Port > 0 ? source.Port : 5432;
                if (!string.IsNullOrWhiteSpace(effectiveDatabase)) builder["Database"] = effectiveDatabase;
                builder["Username"] = source.SqlUsername ?? string.Empty;
                builder["Password"] = password;
                builder["Timeout"] = 15;
                builder["Pooling"] = true;
                builder["Maximum Pool Size"] = 2;
                builder["SSL Mode"] = source.TrustServerCertificate ? "Require" : "VerifyFull";
                builder["Trust Server Certificate"] = source.TrustServerCertificate;
                break;
            case DatabaseEngine.MySql:
                builder["Server"] = source.ServerName;
                builder["Port"] = source.Port > 0 ? source.Port : 3306;
                if (!string.IsNullOrWhiteSpace(effectiveDatabase)) builder["Database"] = effectiveDatabase;
                builder["User ID"] = source.SqlUsername ?? string.Empty;
                builder["Password"] = password;
                builder["Connection Timeout"] = 15;
                builder["Pooling"] = true;
                builder["Maximum Pool Size"] = 2;
                builder["SslMode"] = source.TrustServerCertificate ? "Required" : "VerifyFull";
                break;
            case DatabaseEngine.Db2:
                // Db2 connections are TLS-only in SQL DAM. Use the conventional
                // Db2 SSL listener when the source does not specify a port.
                builder["Server"] = $"{source.ServerName}:{(source.Port > 0 ? source.Port : 50001)}";
                builder["Database"] = effectiveDatabase ?? string.Empty;
                builder["UID"] = source.SqlUsername ?? string.Empty;
                builder["PWD"] = password;
                builder["Connect Timeout"] = 15;
                builder["Security"] = "SSL";
                if (!string.IsNullOrWhiteSpace(source.TlsCertificatePath))
                    builder["SSLServerCertificate"] = source.TlsCertificatePath;
                break;
            default:
                throw new NotSupportedException($"Database engine '{source.Engine}' is not supported.");
        }

        return builder.ConnectionString;
    }

    private static void ValidateSourceSettings(SqlSource source)
    {
        if (string.IsNullOrWhiteSpace(source.ServerName))
            throw new InvalidOperationException("A server host or endpoint is required.");
        if (source.Engine is DatabaseEngine.PostgreSql or DatabaseEngine.Db2 && string.IsNullOrWhiteSpace(source.InitialDatabase))
            throw new InvalidOperationException($"A database name is required for {source.Engine} connections.");
        if (source.Engine != DatabaseEngine.SqlServer &&
            (source.Port < 1 || source.Port > 65535) && source.Port != 0)
            throw new InvalidOperationException("The port must be between 1 and 65535.");
    }

    private static void ValidateCertificatePath(SqlSource source)
    {
        if (source.Engine != DatabaseEngine.Db2 || string.IsNullOrWhiteSpace(source.TlsCertificatePath)) return;
        if (!Path.IsPathFullyQualified(source.TlsCertificatePath))
            throw new InvalidOperationException("The Db2 TLS certificate must be an absolute path on the SQL DAM host.");
        if (!File.Exists(source.TlsCertificatePath))
            throw new InvalidOperationException($"The Db2 TLS certificate file was not found on the SQL DAM host: {source.TlsCertificatePath}");
    }

    private static SqlSource Map(SqliteDataReader reader)
    {
        return new SqlSource
        {
            Id = Guid.Parse(reader.GetString(0)),
            TenantId = Guid.Parse(reader.GetString(1)),
            Name = reader.GetString(2),
            ServerName = reader.GetString(3),
            CollectionMode = Enum.TryParse<SourceCollectionMode>(reader.GetString(4), out var colMode) ? colMode : SourceCollectionMode.InProcessCollector,
            AuthMode = Enum.Parse<SqlAuthMode>(reader.GetString(5)),
            SqlUsername = reader.IsDBNull(6) ? null : reader.GetString(6),
            ProtectedPassword = reader.IsDBNull(7) ? null : reader.GetString(7),
            Enabled = reader.GetInt64(8) != 0,
            PollIntervalSeconds = (int)reader.GetInt64(9),
            CreatedAtUtc = DateTime.Parse(reader.GetString(10)).ToUniversalTime(),
            LastPolledAtUtc = reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11)).ToUniversalTime(),
            LastStatus = reader.GetString(12),
            LastError = reader.IsDBNull(13) ? null : reader.GetString(13),
            TrustServerCertificate = reader.GetInt64(14) != 0,
            Engine = Enum.TryParse<DatabaseEngine>(reader.GetString(15), out var engine) ? engine : DatabaseEngine.SqlServer,
            Port = (int)reader.GetInt64(16),
            InitialDatabase = reader.IsDBNull(17) ? null : reader.GetString(17),
            TlsCertificatePath = reader.IsDBNull(18) ? null : reader.GetString(18)
        };
    }
}
