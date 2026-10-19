using Microsoft.Data.Sqlite;
using SqlDam.Web.Data;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

public sealed class AlertService
{
    private readonly LocalDatabase _database;
    private readonly AlertRuleService _ruleService;
    private readonly ActivityEventService _eventService;
    private readonly AuditService _auditService;
    private readonly IAlertEmailQueue _alertEmailQueue;
    private readonly ILogger<AlertService> _logger;

    public AlertService(
        LocalDatabase database,
        AlertRuleService ruleService,
        ActivityEventService eventService,
        AuditService auditService,
        IAlertEmailQueue alertEmailQueue,
        ILogger<AlertService> logger)
    {
        _database = database;
        _ruleService = ruleService;
        _eventService = eventService;
        _auditService = auditService;
        _alertEmailQueue = alertEmailQueue;
        _logger = logger;
    }

    public List<SecurityAlert> GetAlerts(Guid tenantId, AlertStatus? status = null, int take = 100)
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, tenant_id, source_id, source_name, rule_id, rule_name, severity, status, title, description,
                   trigger_event_count, first_seen_utc, last_seen_utc, resolved_at_utc, resolved_by, resolution_notes
            FROM alerts
            WHERE tenant_id = $tenantId AND ($status IS NULL OR status = $status)
            ORDER BY last_seen_utc DESC
            LIMIT $take
            """;
        cmd.Parameters.AddWithValue("$tenantId", tenantId.ToString());
        cmd.Parameters.AddWithValue("$status", (object?)status?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$take", take);

        var list = new List<SecurityAlert>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }

        return list;
    }

    public int GetOpenAlertCount(Guid tenantId)
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM alerts
            WHERE tenant_id = $tenantId AND status IN ('New', 'Acknowledged', 'InInvestigation')
            """;
        cmd.Parameters.AddWithValue("$tenantId", tenantId.ToString());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateAlertStatus(Guid alertId, AlertStatus newStatus, string actor, string? notes, Guid? tenantId = null)
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE alerts
            SET status = $status,
                resolved_at_utc = $resolvedAt,
                resolved_by = $resolvedBy,
                resolution_notes = $notes
            WHERE id = $id AND ($tenantId IS NULL OR tenant_id = $tenantId)
            """;
        var isResolved = newStatus is AlertStatus.Resolved or AlertStatus.FalsePositive;
        cmd.Parameters.AddWithValue("$status", newStatus.ToString());
        cmd.Parameters.AddWithValue("$resolvedAt", isResolved ? DateTime.UtcNow.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$resolvedBy", isResolved ? actor : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", alertId.ToString());
        cmd.Parameters.AddWithValue("$tenantId", (object?)tenantId?.ToString() ?? DBNull.Value);
        if (cmd.ExecuteNonQuery() == 0) return;

        // Retrieve alert tenant to log in audit trail
        var alert = GetAlert(alertId, tenantId);
        if (alert is not null)
        {
            _auditService.Log(
                alert.TenantId,
                actor,
                $"ALERT_{newStatus.ToString().ToUpperInvariant()}",
                "SecurityAlert",
                alertId.ToString(),
                $"Alert '{alert.Title}' status changed to {newStatus}. Notes: {notes ?? "None"}");
        }
    }

    public SecurityAlert? GetAlert(Guid id, Guid? tenantId = null)
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, tenant_id, source_id, source_name, rule_id, rule_name, severity, status, title, description,
                   trigger_event_count, first_seen_utc, last_seen_utc, resolved_at_utc, resolved_by, resolution_notes
            FROM alerts
            WHERE id = $id AND ($tenantId IS NULL OR tenant_id = $tenantId)
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$tenantId", (object?)tenantId?.ToString() ?? DBNull.Value);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>
    /// Evaluates a newly captured or ingested batch of events against active alert rules.
    /// Aggregates incidents and creates or increments alerts without flooding duplicate records.
    /// </summary>
    public int EvaluateEventBatch(Guid tenantId, Guid sourceId, string sourceName, IReadOnlyList<ActivityEvent> events)
    {
        if (events.Count == 0) return 0;

        var rules = _ruleService.GetRules(tenantId).Where(r => r.Enabled).ToList();
        if (rules.Count == 0) return 0;

        var evaluationTime = events.Max(evt => evt.TimestampUtc);
        var maxWindow = rules.Max(r => r.WindowSeconds);
        var minWindowStart = evaluationTime.AddSeconds(-maxWindow);
        var recentEvents = _eventService.GetEventsForEvaluation(sourceId, tenantId, minWindowStart, evaluationTime);
        var triggeredCount = 0;

        foreach (var rule in rules)
        {
            try
            {
                var windowStart = evaluationTime.AddSeconds(-rule.WindowSeconds);
                var windowEvents = recentEvents
                    .Where(evt => evt.TimestampUtc >= windowStart && evt.TimestampUtc <= evaluationTime)
                    .ToList();
                var isTriggered = false;
                var incidentCount = 0;
                string title = string.Empty;
                string description = string.Empty;

                switch (rule.Category)
                {
                    case RuleCategory.BruteForceLogin:
                        var failedLogins = windowEvents.Where(e => e.Category == ActivityCategory.LoginFailure).ToList();
                        if (failedLogins.Count >= rule.ThresholdCount)
                        {
                            isTriggered = true;
                            incidentCount = failedLogins.Count;
                            var targets = string.Join(", ", failedLogins
                                .Select(l => string.IsNullOrWhiteSpace(l.LoginName) ? "unknown login" : l.LoginName)
                                .Distinct()
                                .Take(3));
                            title = $"Brute-force login failure burst ({failedLogins.Count} attempts)";
                            description = $"{failedLogins.Count} failed logins detected on source '{sourceName}' for account(s): {targets}. (Threshold: {rule.ThresholdCount})";
                        }
                        break;

                    case RuleCategory.PrivilegedAccountAccess:
                        var privilegedKeywords = (rule.FilterPattern ?? "sa,admin,sysadmin,administrator,sqladmin")
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        var privLogins = windowEvents
                            .Where(e => e.Category == ActivityCategory.LoginSuccess &&
                                        privilegedKeywords.Any(p => (e.LoginName ?? "").Contains(p, StringComparison.OrdinalIgnoreCase)))
                            .ToList();
                        if (privLogins.Count >= rule.ThresholdCount)
                        {
                            isTriggered = true;
                            incidentCount = privLogins.Count;
                            var users = string.Join(", ", privLogins
                                .Select(l => string.IsNullOrWhiteSpace(l.LoginName) ? "unknown login" : l.LoginName)
                                .Distinct());
                            title = $"Privileged account login: {users}";
                            description = $"Sensitive privileged login detected on '{sourceName}' by account '{users}' from host '{privLogins.FirstOrDefault()?.ClientHostName ?? "unknown"}'.";
                        }
                        break;

                    case RuleCategory.SchemaTampering:
                        var schemaEvents = windowEvents.Where(e => e.Category == ActivityCategory.SchemaChange).ToList();
                        if (schemaEvents.Count >= rule.ThresholdCount)
                        {
                            isTriggered = true;
                            incidentCount = schemaEvents.Count;
                            var first = schemaEvents.First();
                            title = $"Schema modification (DDL) on '{sourceName}'";
                            description = $"Detected DDL operations: {first.Summary}. Total operations: {schemaEvents.Count}.";
                        }
                        break;

                    case RuleCategory.PermissionTampering:
                        var permEvents = windowEvents.Where(e => e.Category is ActivityCategory.PermissionChange or ActivityCategory.PrincipalChange).ToList();
                        if (permEvents.Count >= rule.ThresholdCount)
                        {
                            isTriggered = true;
                            incidentCount = permEvents.Count;
                            title = $"Security permission or role modified on '{sourceName}'";
                            description = $"Detected permission/role alterations: {permEvents.First().Summary}.";
                        }
                        break;

                    case RuleCategory.SevereErrorThreshold:
                        var errors = windowEvents.Where(e => e.Category == ActivityCategory.Error).ToList();
                        if (errors.Count >= rule.ThresholdCount)
                        {
                            isTriggered = true;
                            incidentCount = errors.Count;
                            title = $"Severe database error burst on '{sourceName}'";
                            description = $"{errors.Count} severe database errors captured in poll interval. Example: {errors.First().Summary}";
                        }
                        break;
                }

                if (isTriggered)
                {
                    var newAlert = UpsertAlert(tenantId, sourceId, sourceName, rule, title, description, incidentCount);
                    if (newAlert is not null)
                    {
                        _alertEmailQueue.TryQueue(tenantId, newAlert);
                    }
                    triggeredCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating rule {RuleName} for source {SourceName}", rule.Name, sourceName);
            }
        }

        return triggeredCount;
    }

    private SecurityAlert? UpsertAlert(
        Guid tenantId,
        Guid sourceId,
        string sourceName,
        AlertRule rule,
        string title,
        string description,
        int count)
    {
        using var connection = _database.OpenConnection();

        // Check if an existing open alert for this rule and source already exists
        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = """
            SELECT id, trigger_event_count FROM alerts
            WHERE tenant_id = $tenantId AND source_id = $sourceId AND rule_id = $ruleId
              AND status IN ('New', 'Acknowledged', 'InInvestigation')
            ORDER BY last_seen_utc DESC
            LIMIT 1
            """;
        checkCmd.Parameters.AddWithValue("$tenantId", tenantId.ToString());
        checkCmd.Parameters.AddWithValue("$sourceId", sourceId.ToString());
        checkCmd.Parameters.AddWithValue("$ruleId", rule.Id.ToString());

        string? existingId = null;
        int existingCount = 0;
        using (var reader = checkCmd.ExecuteReader())
        {
            if (reader.Read())
            {
                existingId = reader.GetString(0);
                existingCount = reader.GetInt32(1);
            }
        }

        if (existingId != null)
        {
            // Increment trigger count and update last_seen_utc
            using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = """
                UPDATE alerts
                SET trigger_event_count = $count,
                    last_seen_utc = $now,
                    description = $desc
                WHERE id = $id
                """;
            updateCmd.Parameters.AddWithValue("$count", Math.Max(existingCount, count));
            updateCmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            updateCmd.Parameters.AddWithValue("$desc", description);
            updateCmd.Parameters.AddWithValue("$id", existingId);
            updateCmd.ExecuteNonQuery();
            return null;
        }

        // Create new alert
        var alert = new SecurityAlert
        {
            TenantId = tenantId,
            SourceId = sourceId,
            SourceName = sourceName,
            RuleId = rule.Id,
            RuleName = rule.Name,
            Severity = rule.Severity,
            Status = AlertStatus.New,
            Title = title,
            Description = description,
            TriggerEventCount = count,
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };

        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO alerts
                (id, tenant_id, source_id, source_name, rule_id, rule_name, severity, status, title, description,
                 trigger_event_count, first_seen_utc, last_seen_utc)
            VALUES
                ($id, $tenantId, $sourceId, $sourceName, $ruleId, $ruleName, $severity, $status, $title, $description,
                 $count, $firstSeen, $lastSeen)
            """;
        insertCmd.Parameters.AddWithValue("$id", alert.Id.ToString());
        insertCmd.Parameters.AddWithValue("$tenantId", alert.TenantId.ToString());
        insertCmd.Parameters.AddWithValue("$sourceId", alert.SourceId.ToString());
        insertCmd.Parameters.AddWithValue("$sourceName", alert.SourceName);
        insertCmd.Parameters.AddWithValue("$ruleId", (object?)alert.RuleId?.ToString() ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("$ruleName", alert.RuleName);
        insertCmd.Parameters.AddWithValue("$severity", alert.Severity.ToString());
        insertCmd.Parameters.AddWithValue("$status", alert.Status.ToString());
        insertCmd.Parameters.AddWithValue("$title", alert.Title);
        insertCmd.Parameters.AddWithValue("$description", alert.Description);
        insertCmd.Parameters.AddWithValue("$count", alert.TriggerEventCount);
        insertCmd.Parameters.AddWithValue("$firstSeen", alert.FirstSeenUtc.ToString("O"));
        insertCmd.Parameters.AddWithValue("$lastSeen", alert.LastSeenUtc.ToString("O"));
        insertCmd.ExecuteNonQuery();

        _auditService.Log(
            tenantId,
            "security-engine",
            "ALERT_TRIGGERED",
            "SecurityAlert",
            alert.Id.ToString(),
            $"Triggered {alert.Severity} alert: '{alert.Title}' on {sourceName}");

        return alert;
    }

    private static SecurityAlert Map(SqliteDataReader reader)
    {
        return new SecurityAlert
        {
            Id = Guid.Parse(reader.GetString(0)),
            TenantId = Guid.Parse(reader.GetString(1)),
            SourceId = Guid.Parse(reader.GetString(2)),
            SourceName = reader.GetString(3),
            RuleId = reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
            RuleName = reader.GetString(5),
            Severity = Enum.Parse<AlertSeverity>(reader.GetString(6)),
            Status = Enum.Parse<AlertStatus>(reader.GetString(7)),
            Title = reader.GetString(8),
            Description = reader.GetString(9),
            TriggerEventCount = reader.GetInt32(10),
            FirstSeenUtc = DateTime.Parse(reader.GetString(11)).ToUniversalTime(),
            LastSeenUtc = DateTime.Parse(reader.GetString(12)).ToUniversalTime(),
            ResolvedAtUtc = reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13)).ToUniversalTime(),
            ResolvedBy = reader.IsDBNull(14) ? null : reader.GetString(14),
            ResolutionNotes = reader.IsDBNull(15) ? null : reader.GetString(15)
        };
    }
}
