namespace SqlDam.Web.Models;

public enum SqlAuthMode
{
    WindowsIntegrated,
    SqlLogin
}

public enum DatabaseEngine
{
    SqlServer,
    PostgreSql,
    MySql,
    Db2
}

public enum SourceCollectionMode
{
    InProcessCollector,
    RemoteAgent
}

/// <summary>
/// A monitored relational database source. Credentials are stored encrypted at
/// rest using ASP.NET Core Data Protection.
/// </summary>
public sealed class SqlSource
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; } = TenantConstants.DefaultTenantId;

    public string Name { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    public DatabaseEngine Engine { get; set; } = DatabaseEngine.SqlServer;

    public int Port { get; set; }

    public string? InitialDatabase { get; set; }

    /// <summary>Optional local PEM or DER certificate file used to validate Db2 TLS.</summary>
    public string? TlsCertificatePath { get; set; }

    /// <summary>
    /// Keeps TLS encryption enabled while skipping SQL Server certificate chain
    /// and server-name validation. Use only for trusted private servers when a
    /// trusted certificate cannot yet be configured.
    /// </summary>
    public bool TrustServerCertificate { get; set; }

    public SourceCollectionMode CollectionMode { get; set; } = SourceCollectionMode.InProcessCollector;

    public SqlAuthMode AuthMode { get; set; } = SqlAuthMode.WindowsIntegrated;

    public string? SqlUsername { get; set; }

    /// <summary>Data-protected (encrypted) SQL login password. Null for Windows auth.</summary>
    public string? ProtectedPassword { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>How often the collector polls this source for new events.</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastPolledAtUtc { get; set; }

    public string LastStatus { get; set; } = "Not polled yet";

    public string? LastError { get; set; }
}
