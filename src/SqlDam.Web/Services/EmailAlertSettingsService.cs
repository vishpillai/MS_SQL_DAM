using System.Net.Mail;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using SqlDam.Web.Data;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

/// <summary>Loads and stores per-tenant SMTP settings, protecting relay passwords at rest.</summary>
internal sealed class EmailAlertSettingsService
{
    private readonly LocalDatabase _database;
    private readonly IDataProtector _passwordProtector;
    private readonly EmailAlertSender _sender;
    private readonly AuditService _auditService;

    public EmailAlertSettingsService(
        LocalDatabase database,
        IDataProtectionProvider dataProtectionProvider,
        EmailAlertSender sender,
        AuditService auditService)
    {
        _database = database;
        _passwordProtector = dataProtectionProvider.CreateProtector("SqlDam.EmailAlertSecrets.v1");
        _sender = sender;
        _auditService = auditService;
    }

    public EmailAlertSettings Get(Guid tenantId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT enabled, smtp_host, smtp_port, use_start_tls, sender_address, sender_name,
                   username, protected_password, recipients, minimum_severity
            FROM email_alert_settings WHERE tenant_id = $tenantId
            """;
        command.Parameters.AddWithValue("$tenantId", tenantId.ToString());

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new EmailAlertSettings();

        return new EmailAlertSettings
        {
            Enabled = reader.GetInt32(0) != 0,
            SmtpHost = reader.GetString(1),
            SmtpPort = reader.GetInt32(2),
            UseStartTls = reader.GetInt32(3) != 0,
            SenderAddress = reader.GetString(4),
            SenderName = reader.GetString(5),
            Username = reader.GetString(6),
            HasStoredPassword = !reader.IsDBNull(7) && !string.IsNullOrEmpty(reader.GetString(7)),
            Recipients = reader.GetString(8),
            MinimumSeverity = Enum.Parse<AlertSeverity>(reader.GetString(9))
        };
    }

    public void Save(
        Guid tenantId,
        EmailAlertSettings settings,
        string? password,
        bool clearSavedPassword,
        string actor)
    {
        var normalized = Normalize(settings);
        if (!Enum.IsDefined(normalized.MinimumSeverity))
            throw new InvalidOperationException("Select a valid minimum alert severity.");
        if (normalized.SmtpPort is < 1 or > 65535)
            throw new InvalidOperationException("SMTP port must be between 1 and 65535.");

        using var connection = _database.OpenConnection();
        var oldProtectedPassword = ReadProtectedPassword(connection, tenantId);
        var protectedPassword = clearSavedPassword || string.IsNullOrWhiteSpace(normalized.Username)
            ? null
            : !string.IsNullOrEmpty(password)
                ? _passwordProtector.Protect(password)
                : oldProtectedPassword;

        if (normalized.Enabled)
        {
            _ = BuildDeliveryOptions(normalized, GetPassword(protectedPassword));
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO email_alert_settings
                (tenant_id, enabled, smtp_host, smtp_port, use_start_tls, sender_address, sender_name,
                 username, protected_password, recipients, minimum_severity, updated_at_utc)
            VALUES
                ($tenantId, $enabled, $host, $port, $startTls, $sender, $senderName,
                 $username, $password, $recipients, $severity, $updated)
            ON CONFLICT(tenant_id) DO UPDATE SET
                enabled = excluded.enabled,
                smtp_host = excluded.smtp_host,
                smtp_port = excluded.smtp_port,
                use_start_tls = excluded.use_start_tls,
                sender_address = excluded.sender_address,
                sender_name = excluded.sender_name,
                username = excluded.username,
                protected_password = excluded.protected_password,
                recipients = excluded.recipients,
                minimum_severity = excluded.minimum_severity,
                updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$tenantId", tenantId.ToString());
        command.Parameters.AddWithValue("$enabled", normalized.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$host", normalized.SmtpHost);
        command.Parameters.AddWithValue("$port", normalized.SmtpPort);
        command.Parameters.AddWithValue("$startTls", normalized.UseStartTls ? 1 : 0);
        command.Parameters.AddWithValue("$sender", normalized.SenderAddress);
        command.Parameters.AddWithValue("$senderName", normalized.SenderName);
        command.Parameters.AddWithValue("$username", normalized.Username);
        command.Parameters.AddWithValue("$password", (object?)protectedPassword ?? DBNull.Value);
        command.Parameters.AddWithValue("$recipients", normalized.Recipients);
        command.Parameters.AddWithValue("$severity", normalized.MinimumSeverity.ToString());
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();

        _auditService.Log(
            tenantId,
            actor,
            "EMAIL_ALERT_SETTINGS_SAVED",
            "EmailAlertSettings",
            tenantId.ToString(),
            $"Saved SMTP alert settings (enabled: {normalized.Enabled}; minimum severity: {normalized.MinimumSeverity}).");
    }

    public async Task SendTestAsync(
        Guid tenantId,
        EmailAlertSettings settings,
        string? password,
        bool clearSavedPassword,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(settings);
        string? effectivePassword;
        if (!string.IsNullOrEmpty(password))
        {
            effectivePassword = password;
        }
        else
        {
            var storedPassword = clearSavedPassword ? null : ReadProtectedPassword(tenantId);
            effectivePassword = GetPassword(storedPassword);
        }

        var options = BuildDeliveryOptions(normalized, effectivePassword);
        await _sender.SendTestAsync(options, cancellationToken);
    }

    internal EmailDeliveryOptions? GetDeliveryOptions(Guid tenantId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT enabled, smtp_host, smtp_port, use_start_tls, sender_address, sender_name,
                   username, protected_password, recipients, minimum_severity
            FROM email_alert_settings WHERE tenant_id = $tenantId
            """;
        command.Parameters.AddWithValue("$tenantId", tenantId.ToString());

        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) == 0) return null;

        var settings = new EmailAlertSettings
        {
            Enabled = true,
            SmtpHost = reader.GetString(1),
            SmtpPort = reader.GetInt32(2),
            UseStartTls = reader.GetInt32(3) != 0,
            SenderAddress = reader.GetString(4),
            SenderName = reader.GetString(5),
            Username = reader.GetString(6),
            Recipients = reader.GetString(8),
            MinimumSeverity = Enum.Parse<AlertSeverity>(reader.GetString(9))
        };
        var protectedPassword = reader.IsDBNull(7) ? null : reader.GetString(7);
        return BuildDeliveryOptions(settings, GetPassword(protectedPassword));
    }

    private string? ReadProtectedPassword(Guid tenantId)
    {
        using var connection = _database.OpenConnection();
        return ReadProtectedPassword(connection, tenantId);
    }

    private static string? ReadProtectedPassword(SqliteConnection connection, Guid tenantId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT protected_password FROM email_alert_settings WHERE tenant_id = $tenantId";
        command.Parameters.AddWithValue("$tenantId", tenantId.ToString());
        return command.ExecuteScalar() as string;
    }

    private string? GetPassword(string? protectedPassword) =>
        string.IsNullOrEmpty(protectedPassword) ? null : _passwordProtector.Unprotect(protectedPassword);

    private static EmailAlertSettings Normalize(EmailAlertSettings settings) => new()
    {
        Enabled = settings.Enabled,
        SmtpHost = (settings.SmtpHost ?? string.Empty).Trim(),
        SmtpPort = settings.SmtpPort,
        UseStartTls = settings.UseStartTls,
        SenderAddress = (settings.SenderAddress ?? string.Empty).Trim(),
        SenderName = string.IsNullOrWhiteSpace(settings.SenderName) ? "SQL DAM" : settings.SenderName.Trim(),
        Username = (settings.Username ?? string.Empty).Trim(),
        Recipients = (settings.Recipients ?? string.Empty).Trim(),
        MinimumSeverity = settings.MinimumSeverity
    };

    private static EmailDeliveryOptions BuildDeliveryOptions(EmailAlertSettings settings, string? password)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
            throw new InvalidOperationException("Enter the SMTP server host.");
        if (settings.SmtpPort is < 1 or > 65535)
            throw new InvalidOperationException("SMTP port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(settings.SenderAddress))
            throw new InvalidOperationException("Enter the sender email address.");
        if (!Enum.IsDefined(settings.MinimumSeverity))
            throw new InvalidOperationException("Select a valid minimum alert severity.");

        ValidateAddress(settings.SenderAddress, "sender");
        var recipients = ParseRecipients(settings.Recipients);
        if (recipients.Count == 0)
            throw new InvalidOperationException("Enter at least one alert recipient email address.");
        if (recipients.Count > 20)
            throw new InvalidOperationException("Enter no more than 20 alert recipients.");
        if (!string.IsNullOrWhiteSpace(settings.Username) && string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Enter an SMTP password for the configured username, or clear the username to use an unauthenticated relay.");
        if (string.IsNullOrWhiteSpace(settings.Username) && !string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Enter the SMTP username for the supplied password.");
        if (!string.IsNullOrWhiteSpace(settings.Username) && !settings.UseStartTls)
            throw new InvalidOperationException("Enable STARTTLS when using SMTP username and password authentication.");

        return new EmailDeliveryOptions(
            settings.SmtpHost,
            settings.SmtpPort,
            settings.UseStartTls,
            settings.SenderAddress,
            settings.SenderName,
            settings.Username,
            password,
            recipients,
            settings.MinimumSeverity);
    }

    private static List<string> ParseRecipients(string recipients)
    {
        var parsed = recipients
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var address in parsed)
        {
            ValidateAddress(address, "recipient");
        }

        return parsed;
    }

    private static void ValidateAddress(string address, string fieldName)
    {
        try
        {
            _ = new MailAddress(address);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"Enter a valid {fieldName} email address.");
        }
    }
}
