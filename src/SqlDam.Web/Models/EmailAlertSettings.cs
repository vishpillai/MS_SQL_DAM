namespace SqlDam.Web.Models;

/// <summary>SMTP delivery settings shown to Super Admins. The SMTP password is never returned here.</summary>
public sealed class EmailAlertSettings
{
    public bool Enabled { get; set; }

    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    public bool UseStartTls { get; set; } = true;

    public string SenderAddress { get; set; } = string.Empty;

    public string SenderName { get; set; } = "SQL DAM";

    public string Username { get; set; } = string.Empty;

    public string Recipients { get; set; } = string.Empty;

    public AlertSeverity MinimumSeverity { get; set; } = AlertSeverity.High;

    public bool HasStoredPassword { get; set; }
}
