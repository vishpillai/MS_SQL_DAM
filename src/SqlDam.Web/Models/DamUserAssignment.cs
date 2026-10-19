namespace SqlDam.Web.Models;

/// <summary>A local DAM role assignment keyed to an OIDC account email.</summary>
public sealed record DamUserAssignment(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
