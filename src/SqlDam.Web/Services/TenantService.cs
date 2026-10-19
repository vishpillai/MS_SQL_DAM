using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SqlDam.Web.Data;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

public sealed class TenantService
{
    private readonly LocalDatabase _database;
    private readonly AuditService _auditService;

    public TenantService(LocalDatabase database, AuditService auditService)
    {
        _database = database;
        _auditService = auditService;
    }

    public List<Tenant> GetAll()
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, identifier, data_retention_days, is_active, created_at_utc, last_agent_heartbeat_utc
            FROM tenants
            ORDER BY created_at_utc ASC
            """;

        var list = new List<Tenant>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }

        return list;
    }

    public Tenant? GetById(Guid id)
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, identifier, data_retention_days, is_active, created_at_utc, last_agent_heartbeat_utc
            FROM tenants
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public Tenant? GetByApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        var keyHash = HashApiKey(apiKey.Trim());

        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, identifier, data_retention_days, is_active, created_at_utc, last_agent_heartbeat_utc
            FROM tenants
            WHERE api_key_hash = $apiKeyHash AND is_active = 1
            """;
        cmd.Parameters.AddWithValue("$apiKeyHash", keyHash);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public Tenant CreateTenant(string name, string identifier, int retentionDays, string actor)
    {
        var tenant = new Tenant
        {
            Name = name.Trim(),
            Identifier = identifier.Trim().ToLowerInvariant(),
            ApiKey = GenerateApiKey(),
            DataRetentionDays = Math.Clamp(retentionDays, 30, 3650),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };

        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, name, identifier, api_key, api_key_hash, data_retention_days, is_active, created_at_utc)
            VALUES ($id, $name, $identifier, $legacyKeyPlaceholder, $apiKeyHash, $retention, 1, $now)
            """;
        cmd.Parameters.AddWithValue("$id", tenant.Id.ToString());
        cmd.Parameters.AddWithValue("$name", tenant.Name);
        cmd.Parameters.AddWithValue("$identifier", tenant.Identifier);
        cmd.Parameters.AddWithValue("$legacyKeyPlaceholder", $"retired:{tenant.Id}");
        cmd.Parameters.AddWithValue("$apiKeyHash", HashApiKey(tenant.ApiKey));
        cmd.Parameters.AddWithValue("$retention", tenant.DataRetentionDays);
        cmd.Parameters.AddWithValue("$now", tenant.CreatedAtUtc.ToString("O"));
        cmd.ExecuteNonQuery();

        // Seed default RBI alert rules for new tenant
        LocalDatabase.SeedDefaultRules(connection, tenant.Id.ToString());

        _auditService.Log(
            tenant.Id,
            actor,
            "TENANT_CREATED",
            "Tenant",
            tenant.Id.ToString(),
            $"Created tenant '{tenant.Name}' with identifier '{tenant.Identifier}' (Retention: {tenant.DataRetentionDays} days)");

        return tenant;
    }

    /// <summary>Applies the organization details collected by the installer once on a new data store.</summary>
    public bool ConfigureInitialOrganization(string name, string identifier, string actor)
    {
        name = name.Trim();
        identifier = identifier.Trim().ToLowerInvariant();
        if (name.Length is < 2 or > 150)
            throw new ArgumentException("Organization name must be between 2 and 150 characters.", nameof(name));
        if (identifier.Length is < 2 or > 100 || identifier.Any(char.IsControl))
            throw new ArgumentException("Organization identifier must be between 2 and 100 printable characters.", nameof(identifier));

        using (var connection = _database.OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using (var state = connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText = "SELECT setting_value FROM app_setup WHERE setting_key = 'initial_organization_complete'";
                if (string.Equals(state.ExecuteScalar() as string, "1", StringComparison.Ordinal))
                {
                    transaction.Commit();
                    return false;
                }
            }

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE tenants SET name = $name, identifier = $identifier WHERE id = $id";
                update.Parameters.AddWithValue("$name", name);
                update.Parameters.AddWithValue("$identifier", identifier);
                update.Parameters.AddWithValue("$id", TenantConstants.DefaultTenantId.ToString());
                if (update.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("The initial organization record was not found.");
            }

            using (var state = connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText = "INSERT INTO app_setup (setting_key, setting_value) VALUES ('initial_organization_complete', '1')";
                state.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        _auditService.Log(TenantConstants.DefaultTenantId, actor, "INITIAL_ORGANIZATION_CONFIGURED",
            "Tenant", TenantConstants.DefaultTenantId.ToString(),
            $"Configured initial organization '{name}' with identifier '{identifier}' from the installer.");
        return true;
    }

    public void UpdateHeartbeat(Guid tenantId)
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE tenants SET last_agent_heartbeat_utc = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", tenantId.ToString());
        cmd.ExecuteNonQuery();
    }

    public string RotateAgentApiKey(Guid tenantId, string actor)
    {
        var tenant = GetById(tenantId) ?? throw new InvalidOperationException("The organization was not found.");
        var newApiKey = GenerateApiKey();
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE tenants SET api_key = $legacyKeyPlaceholder, api_key_hash = $apiKeyHash WHERE id = $id";
        cmd.Parameters.AddWithValue("$legacyKeyPlaceholder", $"retired:{tenant.Id}");
        cmd.Parameters.AddWithValue("$apiKeyHash", HashApiKey(newApiKey));
        cmd.Parameters.AddWithValue("$id", tenant.Id.ToString());
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The organization was not found.");

        _auditService.Log(
            tenantId,
            actor,
            "AGENT_API_KEY_ROTATED",
            "Tenant",
            tenantId.ToString(),
            "Rotated the read-only host agent API key");

        return newApiKey;
    }

    public void UpdateRetention(Guid tenantId, int retentionDays, string actor)
    {
        var tenant = GetById(tenantId);
        if (tenant is null) return;

        var configuredDays = Math.Clamp(retentionDays, 30, 3650);
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE tenants SET data_retention_days = $days WHERE id = $id";
        cmd.Parameters.AddWithValue("$days", configuredDays);
        cmd.Parameters.AddWithValue("$id", tenantId.ToString());
        cmd.ExecuteNonQuery();

        _auditService.Log(
            tenantId,
            actor,
            "RETENTION_UPDATED",
            "Tenant",
            tenantId.ToString(),
            $"Configured data retention to {configuredDays} days");
    }

    private static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return "sqldam_sec_live_" + Convert.ToHexStringLower(bytes);
    }

    private static string HashApiKey(string apiKey)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
    }

    private static Tenant Map(SqliteDataReader reader)
    {
        return new Tenant
        {
            Id = Guid.Parse(reader.GetString(0)),
            Name = reader.GetString(1),
            Identifier = reader.GetString(2),
            ApiKey = string.Empty,
            DataRetentionDays = reader.GetInt32(3),
            IsActive = reader.GetInt64(4) != 0,
            CreatedAtUtc = DateTime.Parse(reader.GetString(5)).ToUniversalTime(),
            LastAgentHeartbeatUtc = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)).ToUniversalTime()
        };
    }
}
