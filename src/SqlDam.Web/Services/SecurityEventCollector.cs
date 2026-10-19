using Microsoft.Data.SqlClient;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

/// <summary>
/// Background collector that polls every enabled SQL source on its own schedule.
/// Collector work is bounded: it reads existing feeds incrementally from file/offset
/// bookmarks, caps each feed page, limits concurrent scans, and backs off failures.
/// It does not create or change audit sessions on monitored servers.
/// </summary>
public sealed class SecurityEventCollector : BackgroundService
{
    // Keep collection work bounded so a monitoring burst cannot fan out into a
    // burst of SQL work on the monitored hosts.
    private const int MaxConcurrentPolls = 2;
    private const int MaxRowsPerFeedPerPoll = 500;
    private const int MaxFailureBackoffSeconds = 300;
    private const int MinimumPollIntervalSeconds = 30;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExtendedEventsReader _reader;
    private readonly SqlAuditReader _auditReader;
    private readonly ILogger<SecurityEventCollector> _logger;
    private readonly Dictionary<Guid, DateTime> _nextPollDue = new();
    private readonly Dictionary<Guid, ActivePoll> _activePolls = new();
    private readonly Dictionary<Guid, int> _consecutiveFailures = new();
    private DateTime _nextRetentionPurgeDue = DateTime.UtcNow.AddMinutes(2);
    private static readonly TimeSpan RetentionPurgeInterval = TimeSpan.FromHours(4);

    public SecurityEventCollector(
        IServiceScopeFactory scopeFactory,
        ExtendedEventsReader reader,
        SqlAuditReader auditReader,
        ILogger<SecurityEventCollector> logger)
    {
        _scopeFactory = scopeFactory;
        _reader = reader;
        _auditReader = auditReader;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try
            {
                ScheduleDueSources(stoppingToken);
                CheckRetentionPurge();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SQL DAM collector tick failed unexpectedly.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        await Task.WhenAll(_activePolls.Values.Select(poll => poll.Task));
    }

    private void CheckRetentionPurge()
    {
        if (DateTime.UtcNow < _nextRetentionPurgeDue) return;
        _nextRetentionPurgeDue = DateTime.UtcNow.Add(RetentionPurgeInterval);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tenantService = scope.ServiceProvider.GetRequiredService<TenantService>();
            var eventService = scope.ServiceProvider.GetRequiredService<ActivityEventService>();

            foreach (var tenant in tenantService.GetAll())
            {
                if (tenant.DataRetentionDays > 0)
                {
                    var purged = eventService.PurgeExpiredEvents(tenant.Id, tenant.DataRetentionDays);
                    if (purged > 0)
                    {
                        _logger.LogInformation("Purged {Count} expired activity events for tenant {TenantName} (retention: {Days} days)",
                            purged, tenant.Name, tenant.DataRetentionDays);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute automated data retention purge.");
        }
    }

    private void ScheduleDueSources(CancellationToken stoppingToken)
    {
        var completedSourceIds = _activePolls
            .Where(pair => pair.Value.Task.IsCompleted)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var sourceId in completedSourceIds)
        {
            var completedPoll = _activePolls[sourceId];
            _activePolls.Remove(sourceId);

            var completionTime = DateTime.UtcNow;
            var pollInterval = Math.Clamp(completedPoll.Source.PollIntervalSeconds, MinimumPollIntervalSeconds, 3600);
            var succeeded = completedPoll.Task.Status == TaskStatus.RanToCompletion && completedPoll.Task.Result;
            if (succeeded)
            {
                _consecutiveFailures.Remove(sourceId);
                _nextPollDue[sourceId] = completionTime.AddSeconds(pollInterval);
            }
            else
            {
                var failures = _consecutiveFailures.GetValueOrDefault(sourceId) + 1;
                _consecutiveFailures[sourceId] = failures;
                var multiplier = 1 << Math.Min(failures, 8);
                var delaySeconds = Math.Min(MaxFailureBackoffSeconds, pollInterval * multiplier);
                _nextPollDue[sourceId] = completionTime.AddSeconds(delaySeconds);
                _logger.LogInformation(
                    "Backing off SQL DAM polling for source {SourceId} for {DelaySeconds} seconds after {FailureCount} unsuccessful poll(s).",
                    sourceId, delaySeconds, failures);
            }
        }

        using var scope = _scopeFactory.CreateScope();
        var sourceService = scope.ServiceProvider.GetRequiredService<SqlSourceService>();
        var now = DateTime.UtcNow;
        var sources = sourceService.GetAll();
        var configuredSourceIds = sources.Select(source => source.Id).ToHashSet();

        foreach (var sourceId in _nextPollDue.Keys.Where(id => !configuredSourceIds.Contains(id)).ToArray())
        {
            _nextPollDue.Remove(sourceId);
            _consecutiveFailures.Remove(sourceId);
        }

        foreach (var source in sources)
        {
            if (!source.Enabled || source.CollectionMode != SourceCollectionMode.InProcessCollector || _activePolls.ContainsKey(source.Id))
            {
                continue;
            }

            if (_activePolls.Count >= MaxConcurrentPolls)
            {
                break;
            }

            // Do not run multiple feed scans against the same configured server at once.
            if (_activePolls.Values.Any(active => string.Equals(
                    active.Source.ServerName.Trim(), source.ServerName.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (_nextPollDue.TryGetValue(source.Id, out var due) && due > now)
            {
                continue;
            }

            var pollInterval = Math.Clamp(source.PollIntervalSeconds, MinimumPollIntervalSeconds, 3600);
            _nextPollDue[source.Id] = now.AddSeconds(pollInterval);
            _activePolls[source.Id] = new ActivePoll(
                source,
                PollSourceAsync(source, stoppingToken));
        }
    }

    private async Task<bool> PollSourceAsync(SqlSource source, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var sourceService = scope.ServiceProvider.GetRequiredService<SqlSourceService>();

        if (source.Engine != DatabaseEngine.SqlServer)
        {
            sourceService.UpdatePollResult(
                source.Id,
                success: false,
                status: $"Waiting for read-only host agent; {source.Engine} audit-feed ingestion is not available yet.",
                error: null);
            return false;
        }

        var eventService = scope.ServiceProvider.GetRequiredService<ActivityEventService>();
        var alertService = scope.ServiceProvider.GetRequiredService<AlertService>();
        var trackedTableService = scope.ServiceProvider.GetRequiredService<TrackedTableService>();

        try
        {
            var connectionString = sourceService.BuildConnectionString(source);

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(stoppingToken);

            var allEvents = new List<ActivityEvent>();
            var mechanismErrors = new List<string>();
            var trackedTables = trackedTableService.GetForSource(source.Id);

            // Read existing Extended Events output only. SQL DAM does not create,
            // alter, start, or stop sessions on the monitored server.
            try
            {
                var (lastFileName, lastFileOffset) = eventService.GetCursor(source.Id, "xe");
                var result = await _reader.ReadNewEventsAsync(
                    connection, source.Id, source.Name, lastFileName, lastFileOffset, stoppingToken,
                    MaxRowsPerFeedPerPoll);

                allEvents.AddRange(AgentActivityFilter.Apply(result.Events, trackedTables));
                if (result.LastFileName is not null && result.LastFileOffset is not null)
                {
                    eventService.SaveCursor(source.Id, result.LastFileName, result.LastFileOffset.Value, "xe");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Extended Events polling failed for SQL source {SourceName}.", source.Name);
                mechanismErrors.Add($"Extended Events: {ex.Message}");
            }

            // Read existing SQL Audit output only. A DBA must configure the
            // server-side audit feed and grant the collector read access.
            try
            {
                var (lastAuditFile, lastAuditOffset) = eventService.GetCursor(source.Id, "audit");
                var auditResult = await _auditReader.ReadNewEventsAsync(
                    connection, source.Id, source.Name, lastAuditFile, lastAuditOffset, stoppingToken,
                    MaxRowsPerFeedPerPoll);

                allEvents.AddRange(AgentActivityFilter.Apply(auditResult.Events, trackedTables));
                if (auditResult.LastFileName is not null && auditResult.LastFileOffset is not null)
                {
                    eventService.SaveCursor(source.Id, auditResult.LastFileName, auditResult.LastFileOffset.Value, "audit");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "SQL Server Audit polling failed for SQL source {SourceName}.", source.Name);
                mechanismErrors.Add($"SQL Server Audit: {ex.Message}");
            }

            if (allEvents.Count > 0)
            {
                eventService.InsertBatch(allEvents, source.TenantId);
                alertService.EvaluateEventBatch(source.TenantId, source.Id, source.Name, allEvents);
            }

            var success = mechanismErrors.Count == 0;
            var status = success
                ? $"OK — {allEvents.Count} new event(s)"
                : $"Partial — {allEvents.Count} new event(s); {string.Join("; ", mechanismErrors)}";
            sourceService.UpdatePollResult(source.Id, success, status, success ? null : string.Join("; ", mechanismErrors));
            return success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Polling SQL source {SourceName} failed.", source.Name);
            sourceService.UpdatePollResult(source.Id, success: false, status: "Error", error: ex.Message);
            return false;
        }
    }

    private sealed record ActivePoll(SqlSource Source, Task<bool> Task);
}
