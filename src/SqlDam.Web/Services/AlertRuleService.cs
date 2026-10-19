using Microsoft.Data.Sqlite;
using SqlDam.Web.Data;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

public sealed class AlertRuleService
{
    private readonly LocalDatabase _database;
    private readonly AuditService _auditService;

    public AlertRuleService(LocalDatabase database, AuditService auditService)
    {
        _database = database;
        _auditService = auditService;
    }

    public List<AlertRule> GetRules(Guid tenantId)
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, tenant_id, name, description, category, severity, enabled, threshold_count, window_seconds, filter_pattern, created_at_utc
            FROM alert_rules
            WHERE tenant_id = $tenantId
            ORDER BY severity DESC, name ASC
            """;
        cmd.Parameters.AddWithValue("$tenantId", tenantId.ToString());

        var list = new List<AlertRule>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }

        return list;
    }

    public AlertRule? GetRule(Guid id)
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, tenant_id, name, description, category, severity, enabled, threshold_count, window_seconds, filter_pattern, created_at_utc
            FROM alert_rules
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public AlertRule AddRule(
        Guid tenantId,
        string name,
        string description,
        RuleCategory category,
        AlertSeverity severity,
        int thresholdCount,
        int windowSeconds,
        string? filterPattern,
        string actor)
    {
        var rule = new AlertRule
        {
            TenantId = tenantId,
            Name = name.Trim(),
            Description = description.Trim(),
            Category = category,
            Severity = severity,
            ThresholdCount = Math.Max(1, thresholdCount),
            WindowSeconds = Math.Clamp(windowSeconds, 10, 86400),
            FilterPattern = string.IsNullOrWhiteSpace(filterPattern) ? null : filterPattern.Trim()
        };

        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO alert_rules
                (id, tenant_id, name, description, category, severity, enabled, threshold_count, window_seconds, filter_pattern, created_at_utc)
            VALUES
                ($id, $tenantId, $name, $desc, $category, $severity, 1, $threshold, $window, $filter, $now)
            """;
        cmd.Parameters.AddWithValue("$id", rule.Id.ToString());
        cmd.Parameters.AddWithValue("$tenantId", rule.TenantId.ToString());
        cmd.Parameters.AddWithValue("$name", rule.Name);
        cmd.Parameters.AddWithValue("$desc", rule.Description);
        cmd.Parameters.AddWithValue("$category", rule.Category.ToString());
        cmd.Parameters.AddWithValue("$severity", rule.Severity.ToString());
        cmd.Parameters.AddWithValue("$threshold", rule.ThresholdCount);
        cmd.Parameters.AddWithValue("$window", rule.WindowSeconds);
        cmd.Parameters.AddWithValue("$filter", (object?)rule.FilterPattern ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", rule.CreatedAtUtc.ToString("O"));
        cmd.ExecuteNonQuery();

        _auditService.Log(tenantId, actor, "RULE_CREATED", "AlertRule", rule.Id.ToString(), $"Created rule '{rule.Name}' ({rule.Severity}, threshold {rule.ThresholdCount})");

        return rule;
    }

    public void ToggleRule(Guid ruleId, bool enabled, string actor)
    {
        var rule = GetRule(ruleId);
        if (rule is null) return;

        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE alert_rules SET enabled = $enabled WHERE id = $id";
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", ruleId.ToString());
        cmd.ExecuteNonQuery();

        _auditService.Log(rule.TenantId, actor, enabled ? "RULE_ENABLED" : "RULE_DISABLED", "AlertRule", ruleId.ToString(), $"Toggled rule '{rule.Name}' to {(enabled ? "enabled" : "disabled")}");
    }

    public void DeleteRule(Guid ruleId, string actor)
    {
        var rule = GetRule(ruleId);
        if (rule is null) return;

        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM alert_rules WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", ruleId.ToString());
        cmd.ExecuteNonQuery();

        _auditService.Log(rule.TenantId, actor, "RULE_DELETED", "AlertRule", ruleId.ToString(), $"Deleted rule '{rule.Name}'");
    }

    private static AlertRule Map(SqliteDataReader reader)
    {
        return new AlertRule
        {
            Id = Guid.Parse(reader.GetString(0)),
            TenantId = Guid.Parse(reader.GetString(1)),
            Name = reader.GetString(2),
            Description = reader.GetString(3),
            Category = Enum.Parse<RuleCategory>(reader.GetString(4)),
            Severity = Enum.Parse<AlertSeverity>(reader.GetString(5)),
            Enabled = reader.GetInt64(6) != 0,
            ThresholdCount = (int)reader.GetInt64(7),
            WindowSeconds = (int)reader.GetInt64(8),
            FilterPattern = reader.IsDBNull(9) ? null : reader.GetString(9),
            CreatedAtUtc = DateTime.Parse(reader.GetString(10)).ToUniversalTime()
        };
    }
}
