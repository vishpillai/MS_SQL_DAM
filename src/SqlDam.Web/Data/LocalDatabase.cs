using Microsoft.Data.Sqlite;

namespace SqlDam.Web.Data;

/// <summary>
/// Owns SQL DAM's local SQLite store (its own data, never the monitored SQL Server).
/// SQLite is used because it is a lightweight, zero-install, file-based store that
/// adds no operational footprint of its own.
/// </summary>
public sealed class LocalDatabase
{
    public string DatabasePath { get; }

    private readonly string _connectionString;

    public LocalDatabase(IConfiguration configuration, IHostEnvironment environment)
    {
        var dataDirectory = configuration["Storage:DataDirectory"];
        var directory = string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(environment.ContentRootPath, "App_Data")
            : dataDirectory;

        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "sqldam.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Initialize();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var walPragma = connection.CreateCommand())
        {
            walPragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
            walPragma.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tenants (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                identifier TEXT NOT NULL UNIQUE,
                api_key TEXT NOT NULL UNIQUE DEFAULT '',
                api_key_hash TEXT NULL,
                data_retention_days INTEGER NOT NULL,
                is_active INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                last_agent_heartbeat_utc TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS sources (
                id TEXT PRIMARY KEY,
                tenant_id TEXT NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
                name TEXT NOT NULL,
                server_name TEXT NOT NULL,
                collection_mode TEXT NOT NULL DEFAULT 'InProcessCollector',
                auth_mode TEXT NOT NULL,
                sql_username TEXT NULL,
                protected_password TEXT NULL,
                enabled INTEGER NOT NULL,
                poll_interval_seconds INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                last_polled_at_utc TEXT NULL,
                last_status TEXT NOT NULL,
                last_error TEXT NULL,
                trust_server_certificate INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS source_cursors (
                source_id TEXT NOT NULL,
                mechanism TEXT NOT NULL DEFAULT 'xe',
                last_file_name TEXT NULL,
                last_file_offset INTEGER NULL,
                PRIMARY KEY (source_id, mechanism),
                FOREIGN KEY (source_id) REFERENCES sources (id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id TEXT NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
                source_id TEXT NOT NULL,
                source_name TEXT NOT NULL,
                event_name TEXT NOT NULL,
                category TEXT NOT NULL,
                timestamp_utc TEXT NOT NULL,
                login_name TEXT NULL,
                client_host_name TEXT NULL,
                client_app_name TEXT NULL,
                database_name TEXT NULL,
                object_name TEXT NULL,
                summary TEXT NOT NULL,
                detail_xml TEXT NOT NULL,
                FOREIGN KEY (source_id) REFERENCES sources (id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS alert_rules (
                id TEXT PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                name TEXT NOT NULL,
                description TEXT NOT NULL,
                category TEXT NOT NULL,
                severity TEXT NOT NULL,
                enabled INTEGER NOT NULL,
                threshold_count INTEGER NOT NULL,
                window_seconds INTEGER NOT NULL,
                filter_pattern TEXT NULL,
                created_at_utc TEXT NOT NULL,
                FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS alerts (
                id TEXT PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                source_id TEXT NOT NULL,
                source_name TEXT NOT NULL,
                rule_id TEXT NULL,
                rule_name TEXT NOT NULL,
                severity TEXT NOT NULL,
                status TEXT NOT NULL,
                title TEXT NOT NULL,
                description TEXT NOT NULL,
                trigger_event_count INTEGER NOT NULL,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                resolved_at_utc TEXT NULL,
                resolved_by TEXT NULL,
                resolution_notes TEXT NULL,
                FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS email_alert_settings (
                tenant_id TEXT PRIMARY KEY,
                enabled INTEGER NOT NULL DEFAULT 0,
                smtp_host TEXT NOT NULL DEFAULT '',
                smtp_port INTEGER NOT NULL DEFAULT 587,
                use_start_tls INTEGER NOT NULL DEFAULT 1,
                sender_address TEXT NOT NULL DEFAULT '',
                sender_name TEXT NOT NULL DEFAULT 'SQL DAM',
                username TEXT NOT NULL DEFAULT '',
                protected_password TEXT NULL,
                recipients TEXT NOT NULL DEFAULT '',
                minimum_severity TEXT NOT NULL DEFAULT 'High',
                updated_at_utc TEXT NOT NULL,
                FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS tracked_tables (
                id TEXT PRIMARY KEY,
                tenant_id TEXT NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111',
                source_id TEXT NOT NULL,
                database_name TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                capture_ddl INTEGER NOT NULL DEFAULT 0,
                capture_dml INTEGER NOT NULL DEFAULT 1,
                created_at_utc TEXT NOT NULL,
                UNIQUE (source_id, database_name, schema_name, table_name),
                FOREIGN KEY (source_id) REFERENCES sources (id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS tracked_table_databases (
                -- Bookkeeping only: remembers every database a source has ever had
                -- DML tracking configured for, even after all its tables are later
                -- deselected, so the collector still revisits it and disables the
                -- per-database audit specification instead of leaving it orphaned
                -- with stale coverage.
                source_id TEXT NOT NULL,
                database_name TEXT NOT NULL,
                PRIMARY KEY (source_id, database_name),
                FOREIGN KEY (source_id) REFERENCES sources (id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS dam_users (
                id TEXT PRIMARY KEY,
                email TEXT NOT NULL COLLATE NOCASE UNIQUE,
                display_name TEXT NOT NULL,
                role TEXT NOT NULL,
                is_active INTEGER NOT NULL DEFAULT 1,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_setup (
                setting_key TEXT PRIMARY KEY,
                setting_value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS audit_log (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id TEXT NOT NULL,
                timestamp_utc TEXT NOT NULL,
                actor TEXT NOT NULL,
                action TEXT NOT NULL,
                entity_type TEXT NOT NULL,
                entity_id TEXT NULL,
                details TEXT NOT NULL,
                client_ip TEXT NULL,
                previous_hash TEXT NOT NULL,
                record_hash TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_events_timestamp ON events (timestamp_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_events_source ON events (source_id);
            CREATE INDEX IF NOT EXISTS ix_events_tenant ON events (tenant_id, timestamp_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_events_tenant_source_time ON events (tenant_id, source_id, timestamp_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_tracked_tables_source ON tracked_tables (source_id, database_name);
            CREATE INDEX IF NOT EXISTS ix_alerts_tenant ON alerts (tenant_id, status);
            CREATE INDEX IF NOT EXISTS ix_alerts_tenant_source_rule ON alerts (tenant_id, source_id, rule_id, status);
            CREATE INDEX IF NOT EXISTS ix_audit_tenant ON audit_log (tenant_id, sequence DESC);
            """;
        command.ExecuteNonQuery();

        MigrateAndSeed(connection);
        connection.Close();
    }

    private static void MigrateAndSeed(SqliteConnection connection)
    {
        // Safe column additions for existing installations
        AddColumnIfNotExists(connection, "sources", "tenant_id", "TEXT NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111'");
        AddColumnIfNotExists(connection, "sources", "collection_mode", "TEXT NOT NULL DEFAULT 'InProcessCollector'");
        AddColumnIfNotExists(connection, "sources", "trust_server_certificate", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfNotExists(connection, "sources", "database_engine", "TEXT NOT NULL DEFAULT 'SqlServer'");
        AddColumnIfNotExists(connection, "sources", "port", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfNotExists(connection, "sources", "initial_database", "TEXT NULL");
        AddColumnIfNotExists(connection, "sources", "tls_certificate_path", "TEXT NULL");
        AddColumnIfNotExists(connection, "events", "tenant_id", "TEXT NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111'");
        AddColumnIfNotExists(connection, "tracked_tables", "capture_ddl", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfNotExists(connection, "tracked_tables", "capture_dml", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfNotExists(connection, "tenants", "api_key_hash", "TEXT NULL");
        using (var retireLegacyKeys = connection.CreateCommand())
        {
            retireLegacyKeys.CommandText = "UPDATE tenants SET api_key = 'retired:' || id WHERE api_key IS NOT NULL AND api_key NOT LIKE 'retired:%'";
            retireLegacyKeys.ExecuteNonQuery();
        }
        using (var index = connection.CreateCommand())
        {
            index.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_tenants_api_key_hash ON tenants(api_key_hash) WHERE api_key_hash IS NOT NULL";
            index.ExecuteNonQuery();
        }

        // Seed default tenant if not exists
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO tenants (id, name, identifier, api_key, data_retention_days, is_active, created_at_utc)
                VALUES (
                    '11111111-1111-1111-1111-111111111111',
                    'Default Organization',
                    'default-organization',
                    'retired:11111111-1111-1111-1111-111111111111',
                    180,
                    1,
                    $now
                );
                """;
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        // Seed standard RBI alert rules for default tenant if not present
        using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM alert_rules WHERE tenant_id = '11111111-1111-1111-1111-111111111111'";
            if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
            {
                SeedDefaultRules(connection, "11111111-1111-1111-1111-111111111111");
            }
        }
    }

    public static void SeedDefaultRules(SqliteConnection connection, string tenantId)
    {
        var rules = new[]
        {
            (
                Guid.NewGuid().ToString(),
                "Brute-force login failure burst",
                "Triggers when repeated failed logins exceed the configured threshold within a time window.",
                "BruteForceLogin",
                "High",
                5,
                60,
                (string?)null
            ),
            (
                Guid.NewGuid().ToString(),
                "Privileged account access detected",
                "Alerts on usage of configured sensitive privileged logins.",
                "PrivilegedAccountAccess",
                "Medium",
                1,
                60,
                "sa,sysadmin,admin,administrator,sqladmin"
            ),
            (
                Guid.NewGuid().ToString(),
                "Schema modification detected",
                "Alerts on CREATE, ALTER, or DROP operations where schema events are available.",
                "SchemaTampering",
                "High",
                1,
                60,
                (string?)null
            ),
            (
                Guid.NewGuid().ToString(),
                "Security permission or role change detected",
                "Detects permission or role changes where the target SQL Server exposes the relevant events.",
                "PermissionTampering",
                "Critical",
                1,
                60,
                (string?)null
            ),
            (
                Guid.NewGuid().ToString(),
                "Severe database error burst",
                "Alerts when captured severity 16+ errors exceed the configured threshold.",
                "SevereErrorThreshold",
                "High",
                3,
                120,
                (string?)null
            )
        };

        foreach (var (id, name, desc, category, severity, threshold, window, filter) in rules)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO alert_rules (id, tenant_id, name, description, category, severity, enabled, threshold_count, window_seconds, filter_pattern, created_at_utc)
                VALUES ($id, $tenantId, $name, $desc, $category, $severity, 1, $thresh, $window, $filter, $now);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$tenantId", tenantId);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$desc", desc);
            cmd.Parameters.AddWithValue("$category", category);
            cmd.Parameters.AddWithValue("$severity", severity);
            cmd.Parameters.AddWithValue("$thresh", threshold);
            cmd.Parameters.AddWithValue("$window", window);
            cmd.Parameters.AddWithValue("$filter", (object?)filter ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    private static void AddColumnIfNotExists(SqliteConnection connection, string table, string column, string definition)
    {
        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = checkCmd.ExecuteReader();
        var exists = false;
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }

        if (!exists)
        {
            using var addCmd = connection.CreateCommand();
            addCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
            addCmd.ExecuteNonQuery();
        }
    }
}
