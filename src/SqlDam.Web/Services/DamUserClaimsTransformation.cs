using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

/// <summary>Applies a local DAM role assignment to an authenticated OIDC account.</summary>
public sealed class DamUserClaimsTransformation : IClaimsTransformation
{
    private static readonly HashSet<string> DamRoleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        DamRoles.Admin,
        DamRoles.Auditor,
        DamRoles.ReadUser,
        DamRoles.SuperAdmin
    };

    private readonly DamUserService _users;
    private readonly string _roleClaimType;
    private readonly string _emailClaimType;

    public DamUserClaimsTransformation(DamUserService users, IConfiguration configuration)
    {
        _users = users;
        _roleClaimType = string.IsNullOrWhiteSpace(configuration["Authentication:RoleClaimType"])
            ? "roles"
            : configuration["Authentication:RoleClaimType"]!;
        _emailClaimType = string.IsNullOrWhiteSpace(configuration["Authentication:EmailClaimType"])
            ? "email"
            : configuration["Authentication:EmailClaimType"]!;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return Task.FromResult(principal);
        }

        var email = principal.FindFirst(_emailClaimType)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst("upn")?.Value;
        var assignment = email is null ? null : _users.FindActiveByEmail(email);
        if (assignment is null)
        {
            return Task.FromResult(principal);
        }

        foreach (var identity in principal.Identities)
        {
            foreach (var claim in identity.Claims
                         .Where(claim => (claim.Type == _roleClaimType || claim.Type == ClaimTypes.Role) && DamRoleNames.Contains(claim.Value))
                         .ToArray())
            {
                identity.RemoveClaim(claim);
            }
        }

        var targetIdentity = principal.Identities.FirstOrDefault(identity => identity.IsAuthenticated)
            ?? principal.Identities.First();
        targetIdentity.AddClaim(new Claim(_roleClaimType, assignment.Role));
        return Task.FromResult(principal);
    }
}
