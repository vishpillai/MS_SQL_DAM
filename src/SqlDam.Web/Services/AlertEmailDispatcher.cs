using System.Threading.Channels;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

public interface IAlertEmailQueue
{
    bool TryQueue(Guid tenantId, SecurityAlert alert);
}

/// <summary>Delivers alert mail away from database polling and event ingestion paths.</summary>
internal sealed class AlertEmailDispatcher : BackgroundService, IAlertEmailQueue
{
    private const int QueueCapacity = 128;
    private readonly Channel<QueuedAlertEmail> _queue = Channel.CreateBounded<QueuedAlertEmail>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EmailAlertSender _sender;
    private readonly ILogger<AlertEmailDispatcher> _logger;

    public AlertEmailDispatcher(
        IServiceScopeFactory scopeFactory,
        EmailAlertSender sender,
        ILogger<AlertEmailDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _sender = sender;
        _logger = logger;
    }

    public bool TryQueue(Guid tenantId, SecurityAlert alert)
    {
        if (_queue.Writer.TryWrite(new QueuedAlertEmail(tenantId, alert))) return true;

        _logger.LogWarning(
            "The alert email queue is full; email notification for alert {AlertId} was not queued.",
            alert.Id);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settingsService = scope.ServiceProvider.GetRequiredService<EmailAlertSettingsService>();
                var options = settingsService.GetDeliveryOptions(item.TenantId);
                if (options is null || item.Alert.Severity < options.MinimumSeverity)
                {
                    continue;
                }

                await SendWithRetryAsync(options, item.Alert, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Email notification failed for SQL DAM alert {AlertId}.", item.Alert.Id);
            }
        }
    }

    private async Task SendWithRetryAsync(
        EmailDeliveryOptions options,
        SecurityAlert alert,
        CancellationToken cancellationToken)
    {
        var delays = new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10) };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _sender.SendAlertAsync(options, alert, cancellationToken);
                _logger.LogInformation("Email notification sent for SQL DAM alert {AlertId}.", alert.Id);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < delays.Length)
            {
                _logger.LogWarning(ex,
                    "Email attempt {Attempt} failed for SQL DAM alert {AlertId}; retrying.", attempt + 1, alert.Id);
                await Task.Delay(delays[attempt], cancellationToken);
            }
        }
    }

    private sealed record QueuedAlertEmail(Guid TenantId, SecurityAlert Alert);
}
