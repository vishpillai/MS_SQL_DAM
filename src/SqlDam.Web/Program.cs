using SqlDam.Web.Components;
using SqlDam.Web.Data;
using SqlDam.Web.Models;
using SqlDam.Web.Services;
using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
if (OperatingSystem.IsWindows())
{
    using var installerSettings = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
        @"SOFTWARE\SqlDam Security\SqlDam");
    if (installerSettings is not null)
    {
        foreach (var (configurationKey, registryValueName) in new[]
                 {
                     ("Storage:DataDirectory", "StorageDataDirectory"),
                     ("Bootstrap:OrganizationName", "BootstrapOrganizationName"),
                     ("Bootstrap:OrganizationIdentifier", "BootstrapOrganizationIdentifier"),
                     ("Bootstrap:SuperAdminEmail", "BootstrapSuperAdminEmail")
                 })
        {
            if (string.IsNullOrWhiteSpace(builder.Configuration[configurationKey]) &&
                installerSettings.GetValue(registryValueName) is string value &&
                !string.IsNullOrWhiteSpace(value))
            {
                builder.Configuration[configurationKey] = value;
            }
        }
    }
}
if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService();
}
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5055");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IClaimsTransformation, DamUserClaimsTransformation>();
var developmentMode = builder.Environment.IsDevelopment();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(DamPolicies.Read, policy =>
    {
        if (developmentMode) policy.RequireAssertion(_ => true);
        else policy.RequireAuthenticatedUser().RequireRole(DamRoles.SuperAdmin, DamRoles.Admin, DamRoles.Auditor, DamRoles.ReadUser);
    });
    options.AddPolicy(DamPolicies.Admin, policy =>
    {
        if (developmentMode) policy.RequireAssertion(_ => true);
        else policy.RequireAuthenticatedUser().RequireRole(DamRoles.SuperAdmin, DamRoles.Admin);
    });
    options.AddPolicy(DamPolicies.SuperAdmin, policy =>
    {
        if (developmentMode) policy.RequireAssertion(_ => true);
        else policy.RequireAuthenticatedUser().RequireRole(DamRoles.SuperAdmin);
    });
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var configuredDataDirectory = builder.Configuration["Storage:DataDirectory"];
var dataDirectory = string.IsNullOrWhiteSpace(configuredDataDirectory)
    ? Path.Combine(builder.Environment.ContentRootPath, "App_Data")
    : configuredDataDirectory;
Directory.CreateDirectory(dataDirectory);

// Keep the established Windows key path for compatibility with existing DPAPI
// protected credentials. On Linux, keep the key ring under the service's private
// data directory; the installer locks that directory to the sqldam service account.
var configuredKeyDirectory = builder.Configuration["DataProtection:KeysDirectory"];
var keyDirectory = !string.IsNullOrWhiteSpace(configuredKeyDirectory)
    ? configuredKeyDirectory
    : OperatingSystem.IsWindows()
        ? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")
        : Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keyDirectory);

var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
    .SetApplicationName("SqlDam");
if (OperatingSystem.IsWindows())
{
    // Machine-scoped DPAPI lets Windows services retain encrypted credentials even
    // if the configured service account changes during maintenance.
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

builder.Services.AddSingleton<LocalDatabase>();
builder.Services.AddScoped<SourceSecretProtector>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<DamUserService>();
builder.Services.AddScoped<AlertRuleService>();
builder.Services.AddScoped<EmailAlertSettingsService>();
builder.Services.AddSingleton<EmailAlertSender>();
builder.Services.AddSingleton<AlertEmailDispatcher>();
builder.Services.AddSingleton<IAlertEmailQueue>(services => services.GetRequiredService<AlertEmailDispatcher>());
builder.Services.AddHostedService<AlertEmailDispatcher>(services => services.GetRequiredService<AlertEmailDispatcher>());
builder.Services.AddScoped<AlertService>();
builder.Services.AddScoped<TenantService>();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<SqlSourceService>();
builder.Services.AddSingleton<DatabaseConnectionFactory>();
builder.Services.AddScoped<ActivityEventService>();
builder.Services.AddSingleton<SqlServerLogStore>();
builder.Services.AddScoped<TrackedTableService>();
builder.Services.AddSingleton<ExtendedEventsReader>();
builder.Services.AddSingleton<SqlAuditReader>();
builder.Services.AddSingleton<SqlSchemaBrowserService>();
builder.Services.AddHostedService<SecurityEventCollector>();

var oidcAuthority = builder.Configuration["Authentication:Authority"];
var oidcClientId = builder.Configuration["Authentication:ClientId"];
var oidcClientSecret = builder.Configuration["Authentication:ClientSecret"];
var oidcValues = new[] { oidcAuthority, oidcClientId, oidcClientSecret };
var oidcConfigured = oidcValues.All(value => !string.IsNullOrWhiteSpace(value));
var oidcPartiallyConfigured = oidcValues.Any(value => !string.IsNullOrWhiteSpace(value)) && !oidcConfigured;

if (oidcPartiallyConfigured)
{
    throw new InvalidOperationException("Configure Authentication:Authority, Authentication:ClientId, and Authentication:ClientSecret together.");
}

if (!builder.Environment.IsDevelopment() && !oidcConfigured)
{
    throw new InvalidOperationException("OIDC authentication must be configured outside Development.");
}

var agentApiEnabled = builder.Configuration.GetValue<bool>("AgentApi:Enabled");
if (agentApiEnabled && !oidcConfigured)
{
    throw new InvalidOperationException("Agent API requires OIDC to be configured.");
}

if (oidcConfigured)
{
    if (!Uri.TryCreate(oidcAuthority, UriKind.Absolute, out var authorityUri) ||
        (!builder.Environment.IsDevelopment() && authorityUri.Scheme != Uri.UriSchemeHttps))
    {
        throw new InvalidOperationException("Authentication:Authority must be a valid HTTPS URL outside Development.");
    }

    builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            options.DefaultSignOutScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.Cookie.Name = builder.Environment.IsDevelopment() ? "SqlDam.Auth" : "__Host-SqlDam.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = true;
        })
        .AddOpenIdConnect(options =>
        {
            options.Authority = oidcAuthority;
            options.ClientId = oidcClientId;
            options.ClientSecret = oidcClientSecret;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;
            options.SaveTokens = false;
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = "name",
                RoleClaimType = builder.Configuration["Authentication:RoleClaimType"] ?? "roles"
            };
        });
}

var app = builder.Build();

// Initialize and validate an optional central SQL Server event store before the
// collector starts. A bad connection string must fail startup instead of silently
// returning to local-only log storage.
_ = app.Services.GetRequiredService<SqlServerLogStore>();

var bootstrapSuperAdminEmail = app.Configuration["Bootstrap:SuperAdminEmail"];
var bootstrapOrganizationName = app.Configuration["Bootstrap:OrganizationName"];
var bootstrapOrganizationIdentifier = app.Configuration["Bootstrap:OrganizationIdentifier"];
if (string.IsNullOrWhiteSpace(bootstrapOrganizationName) != string.IsNullOrWhiteSpace(bootstrapOrganizationIdentifier))
{
    throw new InvalidOperationException("Configure Bootstrap:OrganizationName and Bootstrap:OrganizationIdentifier together.");
}

if (!string.IsNullOrWhiteSpace(bootstrapOrganizationName) && !string.IsNullOrWhiteSpace(bootstrapOrganizationIdentifier))
{
    using var bootstrapScope = app.Services.CreateScope();
    var tenants = bootstrapScope.ServiceProvider.GetRequiredService<TenantService>();
    if (tenants.ConfigureInitialOrganization(bootstrapOrganizationName, bootstrapOrganizationIdentifier, "installer"))
    {
        app.Logger.LogInformation("Configured the initial organization {OrganizationName} ({OrganizationIdentifier}) from the installer.",
            bootstrapOrganizationName, bootstrapOrganizationIdentifier);
    }
}

if (!string.IsNullOrWhiteSpace(bootstrapSuperAdminEmail))
{
    using (var bootstrapScope = app.Services.CreateScope())
    {
        var damUsers = bootstrapScope.ServiceProvider.GetRequiredService<DamUserService>();
        if (damUsers.BootstrapSuperAdmin(bootstrapSuperAdminEmail))
        {
            app.Logger.LogInformation("Created the initial Super Admin role assignment for {Email}.", bootstrapSuperAdminEmail);
        }
    }
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        // Allow loopback (localhost / 127.0.0.1) requests over plain HTTP for local server dashboard access
        var isLoopback = context.Connection.RemoteIpAddress is null || IPAddress.IsLoopback(context.Connection.RemoteIpAddress);
        if (!context.Request.IsHttps && !isLoopback)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        await next();
    });
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (oidcConfigured)
{
    app.UseAuthentication();
}
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/health", (SqlSourceService sourceService, TenantService tenantService, AlertService alertService, SqlServerLogStore logStore) =>
{
    var tenants = tenantService.GetAll();
    var sources = sourceService.GetAll();
    var monitoring = sources.Count == 0
        ? "not-configured"
        : sources.Any(s => s.Enabled) ? "configured" : "configured-but-disabled";

    var defaultTenantId = tenants.FirstOrDefault()?.Id ?? TenantConstants.DefaultTenantId;
    var openAlerts = alertService.GetOpenAlertCount(defaultTenantId);

    return Results.Ok(new
    {
        status = "ok",
        monitoring,
        tenantCount = tenants.Count,
        sourceCount = sources.Count,
        openAlerts,
        logStore = logStore.ProviderName,
        deploymentModel = "on-premises",
        complianceStatus = "not-assessed"
    });
});

if (agentApiEnabled)
{
    app.MapGet("/api/v1/agent/ping", (HttpContext context, TenantService tenantService) =>
    {
        var apiKey = context.Request.Headers["X-API-Key"].ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                apiKey = authHeader["Bearer ".Length..].Trim();
            }
        }

        var tenant = tenantService.GetByApiKey(apiKey);
        if (tenant is null)
        {
            return Results.Unauthorized();
        }

        tenantService.UpdateHeartbeat(tenant.Id);
        return Results.Ok(new
        {
            status = "ok",
            tenantId = tenant.Id,
            tenantName = tenant.Name,
            serverTimeUtc = DateTime.UtcNow
        });
    });

    app.MapPost("/api/v1/agent/ingest", (
        HttpContext context,
        AgentIngestBatch batch,
        TenantService tenantService,
        SqlSourceService sourceService,
        ActivityEventService eventService,
        AlertService alertService,
        TrackedTableService trackedTableService) =>
    {
        var apiKey = context.Request.Headers["X-API-Key"].ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                apiKey = authHeader["Bearer ".Length..].Trim();
            }
        }

        var tenant = tenantService.GetByApiKey(apiKey);
        if (tenant is null)
        {
            return Results.Unauthorized();
        }

        if (batch.SourceId == Guid.Empty || batch.Events is null || batch.Events.Count > 1000)
        {
            return Results.BadRequest(new { error = "A source ID and at most 1000 events are required." });
        }

        tenantService.UpdateHeartbeat(tenant.Id);

        var source = sourceService.Get(batch.SourceId);
        if (source is not null && (source.TenantId != tenant.Id || source.CollectionMode != SourceCollectionMode.RemoteAgent))
        {
            return Results.NotFound();
        }

        if (source is not null && source.Engine != batch.Engine)
        {
            return Results.BadRequest(new { error = "The reported database engine does not match the configured source." });
        }
        if (source is not null && !source.Enabled)
        {
            return Results.Conflict(new { error = "This source is disabled in SQL DAM." });
        }

        source ??= sourceService.Add(
            tenant.Id,
            string.IsNullOrWhiteSpace(batch.SourceName) ? "Agent Source" : batch.SourceName,
            string.IsNullOrWhiteSpace(batch.ServerName) ? "localhost" : batch.ServerName,
            SourceCollectionMode.RemoteAgent,
            SqlAuthMode.WindowsIntegrated,
            null,
            null,
            30,
            $"agent:{tenant.Identifier}",
            batch.SourceId,
            engine: batch.Engine,
            initialDatabase: batch.DatabaseName);

        var trackedTables = trackedTableService.GetForSource(source.Id);
        var acceptedEvents = AgentActivityFilter.Apply(batch.Events, trackedTables);
        var eventsToInsert = acceptedEvents.Select(e => new ActivityEvent
        {
            TenantId = tenant.Id,
            SourceId = source.Id,
            SourceName = source.Name,
            EventName = e.EventName,
            Category = e.Category,
            TimestampUtc = e.TimestampUtc,
            LoginName = e.LoginName,
            ClientHostName = e.ClientHostName,
            ClientAppName = e.ClientAppName,
            DatabaseName = e.DatabaseName,
            ObjectName = e.ObjectName,
            Summary = e.Summary,
            DetailXml = e.DetailXml
        }).ToList();

        if (eventsToInsert.Count > 0)
        {
            eventService.InsertBatch(eventsToInsert, tenant.Id);
        }

        if (!string.IsNullOrEmpty(batch.LastFileName) && batch.LastFileOffset.HasValue)
        {
            eventService.SaveCursor(source.Id, batch.LastFileName, batch.LastFileOffset.Value);
        }

        var alertsTriggered = alertService.EvaluateEventBatch(tenant.Id, source.Id, source.Name, eventsToInsert);
        sourceService.UpdatePollResult(source.Id, success: true, status: $"Agent ingest: {eventsToInsert.Count} event(s)", error: null);

        return Results.Ok(new AgentIngestResponse
        {
            Success = true,
            IngestedCount = eventsToInsert.Count,
            AlertsTriggered = alertsTriggered,
            Message = $"Successfully ingested {eventsToInsert.Count} events. {alertsTriggered} alert(s) triggered."
        });
    });
}

var integrityEndpoint = app.MapGet("/api/v1/compliance/verify", (TenantService tenantService, AuditService auditService) =>
{
    var tenant = tenantService.GetAll().FirstOrDefault();
    if (tenant is null) return Results.NotFound();

    var result = auditService.VerifyIntegrity(tenant.Id);
    return Results.Ok(result);
});
if (oidcConfigured)
{
    integrityEndpoint.RequireAuthorization(DamPolicies.Read);
}
if (oidcConfigured)
{
    app.MapGet("/login", () => Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/" },
        [OpenIdConnectDefaults.AuthenticationScheme]))
        .AllowAnonymous();

    app.MapGet("/logout", (HttpContext context, IAntiforgery antiforgery) =>
    {
        var requestToken = antiforgery.GetAndStoreTokens(context).RequestToken;
        var encodedToken = WebUtility.HtmlEncode(requestToken);
        return Results.Content($"""<!doctype html><html lang="en"><meta charset="utf-8"><title>Sign out | SQL DAM</title><body><main><h1>Sign out</h1><form method="post" action="/logout"><input type="hidden" name="__RequestVerificationToken" value="{encodedToken}"><button type="submit">Sign out</button></form></main></body></html>""", "text/html");
    })
        .RequireAuthorization();

    app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = "/" });
        return Results.Empty;
    })
        .RequireAuthorization();
}
else
{
    app.MapGet("/logout", () => Results.Redirect("/"));
}

var razorComponents = app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
if (oidcConfigured)
{
    razorComponents.RequireAuthorization();
}

app.Run();
