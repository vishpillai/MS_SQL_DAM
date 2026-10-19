using Microsoft.AspNetCore.DataProtection;

namespace SqlDam.Web.Services;

/// <summary>
/// Encrypts/decrypts SQL login passwords at rest using ASP.NET Core Data Protection,
/// so credentials stored in the local SQLite database are never kept in plain text.
/// </summary>
public sealed class SourceSecretProtector
{
    private readonly IDataProtector _protector;

    public SourceSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("SqlDam.SourceSecrets.v1");
    }

    public string Protect(string plainText) => _protector.Protect(plainText);

    public string Unprotect(string protectedText) => _protector.Unprotect(protectedText);
}
