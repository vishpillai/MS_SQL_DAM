using System.Net;
using System.Net.Mail;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

internal sealed record EmailDeliveryOptions(
    string SmtpHost,
    int SmtpPort,
    bool UseStartTls,
    string SenderAddress,
    string SenderName,
    string Username,
    string? Password,
    IReadOnlyList<string> Recipients,
    AlertSeverity MinimumSeverity);

/// <summary>Sends plain-text SQL DAM alert and test messages through an SMTP relay.</summary>
internal sealed class EmailAlertSender
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

    public Task SendAlertAsync(EmailDeliveryOptions options, SecurityAlert alert, CancellationToken cancellationToken)
    {
        var subject = $"[SQL DAM] {alert.Severity} alert: {SingleLine(alert.Title, 140)}";
        var body = $"""
            A new SQL DAM security alert was created.

            Alert: {alert.Title}
            Severity: {alert.Severity}
            Rule: {alert.RuleName}
            Source: {alert.SourceName}
            Status: {alert.Status}
            First seen (UTC): {alert.FirstSeenUtc:O}
            Last seen (UTC): {alert.LastSeenUtc:O}
            Trigger event count: {alert.TriggerEventCount}

            Details:
            {alert.Description}

            Open SQL DAM to review the alert and its related activity.
            """;

        return SendAsync(options, subject, body, alert.Severity >= AlertSeverity.High, cancellationToken);
    }

    public Task SendTestAsync(EmailDeliveryOptions options, CancellationToken cancellationToken)
    {
        var body = $"""
            This is a test message from SQL DAM.

            SMTP settings are working. New security alerts at or above the configured minimum severity will be sent to this recipient list.
            Sent (UTC): {DateTime.UtcNow:O}
            """;

        return SendAsync(options, "[SQL DAM] Email alert test", body, false, cancellationToken);
    }

    private static async Task SendAsync(
        EmailDeliveryOptions options,
        string subject,
        string body,
        bool highPriority,
        CancellationToken cancellationToken)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(options.SenderAddress, options.SenderName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false,
            Priority = highPriority ? MailPriority.High : MailPriority.Normal
        };

        foreach (var recipient in options.Recipients)
        {
            message.To.Add(new MailAddress(recipient));
        }

        using var client = new SmtpClient(options.SmtpHost, options.SmtpPort)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = options.UseStartTls,
            UseDefaultCredentials = false,
            Timeout = (int)SendTimeout.TotalMilliseconds
        };

        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            client.Credentials = new NetworkCredential(options.Username, options.Password ?? string.Empty);
        }

        await client.SendMailAsync(message, cancellationToken);
    }

    private static string SingleLine(string value, int maxLength)
    {
        var normalized = string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
