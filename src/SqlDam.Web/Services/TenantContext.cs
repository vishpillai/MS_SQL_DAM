using SqlDam.Web.Models;

namespace SqlDam.Web.Services;

/// <summary>
/// Scoped ambient context service for Blazor components representing the currently selected organization/tenant.
/// </summary>
public sealed class TenantContext
{
    private readonly TenantService _tenantService;
    private Tenant? _currentTenant;

    public event Action? OnTenantChanged;

    public TenantContext(TenantService tenantService)
    {
        _tenantService = tenantService;
    }

    public Tenant CurrentTenant
    {
        get
        {
            if (_currentTenant is null)
            {
                var tenants = _tenantService.GetAll();
                _currentTenant = tenants.FirstOrDefault(t => t.Id == TenantConstants.DefaultTenantId)
                                 ?? tenants.FirstOrDefault()
                                 ?? new Tenant
                                 {
                                     Id = TenantConstants.DefaultTenantId,
                                     Name = TenantConstants.DefaultTenantName,
                                     Identifier = TenantConstants.DefaultTenantIdentifier
                                 };
            }
            return _currentTenant;
        }
    }

    public void SetTenant(Tenant tenant)
    {
        _currentTenant = tenant;
        OnTenantChanged?.Invoke();
    }

    public void Refresh()
    {
        _currentTenant = null;
        _ = CurrentTenant;
        OnTenantChanged?.Invoke();
    }
}
