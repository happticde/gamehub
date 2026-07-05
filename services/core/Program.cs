using System.Security.Claims;
using System.Threading.RateLimiting;
using GameHub.Core.Data;
using GameHub.Core.Models.Auth;
using GameHub.Core.Services;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// --- Caching ---
builder.Services.AddMemoryCache();

// --- MVC & Swagger ---
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    const string apiKeyScheme = ApiKeyAuthenticationHandler.SchemeName;
    const string bearerScheme = JwtBearerDefaults.AuthenticationScheme;

    // Security definitions create Swagger UI's "Authorize" dialog. The API key value entered
    // there is sent verbatim in X-Api-Key; it is never copied into the generated document.
    options.AddSecurityDefinition(apiKeyScheme, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = ApiKeyAuthenticationHandler.HeaderName,
        Description = "GameHub server API key. Enter the value configured as Authentication:ApiKey."
    });

    // JWT clients paste only the compact token, without manually prefixing "Bearer ". Swagger
    // UI derives the Authorization header format from the HTTP bearer scheme.
    options.AddSecurityDefinition(bearerScheme, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "JWT access token returned by POST /api/auth/token or /api/auth/refresh."
    });

    // Each requirement object is one alternative in OpenAPI's top-level security array. Keeping
    // them separate documents the composite handler correctly as API key OR bearer token.
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference(apiKeyScheme, document)] = []
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference(bearerScheme, document)] = []
    });
});

// Login endpoints are attractive brute-force targets. This named policy limits each
// remote IP address independently, so one noisy client does not consume the allowance
// for every other client. QueueLimit=0 rejects excess attempts immediately with HTTP 429
// instead of holding password-verification work in memory.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthRateLimitPolicies.Token, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

// --- Health Checks (Improvement #9) ---
// Registers a /health endpoint that checks DB connectivity.
// Useful for Docker HEALTHCHECK, Kubernetes readiness probes, or uptime monitors.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<CoreContext>("database");

// --- HTTP Client for Ingest Service ---
// AddStandardResilienceHandler() adds automatic:
//   - Retries with exponential backoff (default: 3 attempts)
//   - Circuit breaker (stops calling a failing service to let it recover)
//   - Request timeout (prevents hanging forever on slow responses)
//   - Total request timeout (caps the total time including all retries)
var ingestUrl = builder.Configuration["Services:IngestUrl"] ?? "http://localhost:5000";
builder.Services.AddHttpClient<IngestClient>(c =>
{
    c.BaseAddress = new Uri(ingestUrl);
})
    .AddStandardResilienceHandler(options =>
    {
        // Stored platform retrieval may fan out to provider APIs before Ingest can respond.
        options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(5);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
    });
builder.Services.AddScoped<UnificationService>();
builder.Services.AddScoped<PlatformCredentialManagementService>();
builder.Services.AddScoped<AccountDeletionService>();
builder.Services.AddScoped<ActivityLogService>();
builder.Services.AddScoped<BackupExportService>();
builder.Services.AddScoped<BackupImportService>();
builder.Services.AddSingleton<BackupCsvSerializer>();
builder.Services.AddSingleton<BackupJsonSerializer>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<TenantExecutionContext>();
builder.Services.AddScoped<ITenantContext, AuthenticatedTenantContext>();
builder.Services.AddSingleton<ITenantJobRunner, TenantJobRunner>();

// PasswordHasher<TUser> implements ASP.NET's versioned password-hash format. It is
// intentionally used instead of encrypting or manually hashing passwords: hashes are
// one-way, salted, and can be upgraded transparently when ASP.NET changes its defaults.
// The hasher is stateless and safe as a singleton; AccountService is scoped because it
// owns a request-scoped CoreContext.
builder.Services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<ExternalIdentityService>();
builder.Services.AddScoped<OidcJitProvisioningService>();

// Parsing here makes invalid security configuration fail during startup rather than on
// the first login request. An empty signing key is the deliberate opt-out switch, keeping
// existing API-key-only installations backward compatible.
var jwtSettings = JwtSettings.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(jwtSettings);
builder.Services.AddSingleton<JwtTokenService>();
// OIDC configuration is parsed during startup, not lazily on the first callback. A deployment
// that opts in with incomplete issuer/audience/algorithm/BFF trust therefore never starts in a
// partially authenticated state. The metadata provider is a singleton so discovery/JWKS caching
// and signing-key refresh state are shared across requests.
var oidcSettings = OidcSettings.FromConfiguration(builder.Configuration, jwtSettings);
builder.Services.AddSingleton(oidcSettings);
builder.Services.AddSingleton<IOidcMetadataProvider, OidcMetadataProvider>();
builder.Services.AddSingleton<OidcTokenValidator>();
// The guarded switch is independent from OIDC. It is parsed once and validates the mirrored BFF
// state plus every Core trust secret before any request can run. OIDC configuration by itself
// therefore never changes the active authorization policy.
var multiUserSettings = MultiUserEnablementSettings.FromConfiguration(
    builder.Configuration,
    jwtSettings,
    oidcSettings);
builder.Services.AddSingleton(multiUserSettings);
builder.Services.AddSingleton(multiUserSettings.Mode);
builder.Services.AddScoped<MultiUserEnablementService>();
builder.Services.AddScoped<IMultiUserIngestReadinessProbe, MultiUserIngestReadinessProbe>();
// RefreshTokenService is scoped because rotation performs transactional database writes.
// The JWT signing service stays singleton and contains no per-request state.
builder.Services.AddScoped<RefreshTokenService>();

// --- IGDB Metadata Provider (ADR-003) ---
// IgdbClient is a singleton so there's only one OAuth token in memory.
// Multiple instances would each manage their own token, causing races where
// one refreshes and the other still uses the stale token → intermittent 401s.
// We use IHttpClientFactory to get pooled HttpClient instances while keeping
// the IgdbClient itself as a singleton.
builder.Services.AddHttpClient("IgdbClient")
    .AddStandardResilienceHandler();
builder.Services.AddSingleton<IgdbClient>(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var http = httpFactory.CreateClient("IgdbClient");
    var config = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<IgdbClient>>();
    var cache = sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
    return new IgdbClient(http, config, logger, cache);
});
builder.Services.AddSingleton<IGameMetadataProvider>(sp => sp.GetRequiredService<IgdbClient>());
builder.Services.AddSingleton<EnrichmentService>();

// --- Background Enrichment ---
if (builder.Configuration.GetValue("Igdb:EnableBackgroundEnrichment", true))
{
    builder.Services.AddHostedService<EnrichmentBackgroundService>();
}

// --- Background Sync Scheduler (#6) ---
// Runs RunFullSyncAsync() on a configurable interval.
// Disabled by default — set Sync:EnableScheduler=true to activate.
var syncStatus = new SyncSchedulerStatus
{
    IsEnabled = builder.Configuration.GetValue("Sync:EnableScheduler", false)
};
builder.Services.AddSingleton(syncStatus);
if (syncStatus.IsEnabled)
{
    builder.Services.AddHostedService<SyncSchedulerService>();
}

// --- Authentication (ADR-002) ---
// Always register the legacy API-key handler first. It remains the only/default scheme
// when JWT has no signing key, which preserves existing CLI and service-to-service clients.
builder.Services.AddApiKeyAuthentication(builder.Configuration);
if (jwtSettings.IsEnabled)
{
    // A policy scheme is a dispatcher, not another credential format. Controllers can keep
    // using a plain [Authorize] attribute while this selector chooses the concrete handler:
    //   Authorization: Bearer <token> -> JWT Bearer
    //   every other request            -> X-Api-Key
    // Bootstrap explicitly requests the API-key scheme, so a JWT cannot create accounts.
    const string compositeScheme = "GameHub";
    builder.Services.AddAuthentication(options =>
        {
            // AuthenticateScheme builds HttpContext.User. ChallengeScheme decides which
            // handler produces a 401 response when authorization fails.
            options.DefaultAuthenticateScheme = compositeScheme;
            options.DefaultChallengeScheme = compositeScheme;
        })
        .AddPolicyScheme(compositeScheme, null, options =>
        {
            options.ForwardDefaultSelector = context =>
                context.Request.Headers.Authorization.ToString()
                    .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? JwtBearerDefaults.AuthenticationScheme
                    : ApiKeyAuthenticationHandler.SchemeName;
        })
        .AddJwtBearer(options =>
        {
            // Keep the exact claim names written by JwtTokenService. Automatic inbound
            // mapping can rename standard JWT claims and makes tenant resolution harder
            // to reason about when inspecting a token.
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                // The signature proves that GameHub issued the token and that its payload
                // (especially the tenant claim) has not been modified by the client.
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = jwtSettings.CreateSecurityKey(),

                // Issuer identifies this GameHub server; audience identifies the intended
                // clients. Both checks prevent accepting an otherwise valid token created
                // for a different application or deployment.
                ValidateIssuer = true,
                ValidIssuer = jwtSettings.Issuer,
                ValidateAudience = true,
                ValidAudience = jwtSettings.Audience,

                // Expired tokens are rejected. A one-minute skew tolerates small clock
                // differences between machines without materially extending token life.
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),

                // This tells ClaimsPrincipal.Identity which claim represents the display
                // name. Restricting algorithms prevents accepting another signing method
                // merely because it happens to use the same key material.
                NameClaimType = ClaimTypes.Name,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    // A signed token proves what was issued, while this lookup proves that the
                    // account/tenant pair is still live. Pending or completed deletion therefore
                    // invalidates normal API access immediately instead of waiting for JWT expiry.
                    var principal = context.Principal;
                    var accountId = Guid.Empty;
                    var tenantId = Guid.Empty;
                    var hasIdentity = Guid.TryParse(
                            principal?.FindFirstValue(ClaimTypes.NameIdentifier),
                            out accountId)
                        && accountId != Guid.Empty
                        && Guid.TryParse(
                            principal?.FindFirstValue(GameHubClaimTypes.TenantId),
                            out tenantId)
                        && tenantId != Guid.Empty;
                    if (!hasIdentity)
                    {
                        context.Fail("The access token has no valid account identity.");
                        return;
                    }

                    // A locally verified password may bootstrap the first OIDC link for an
                    // operator-provisioned account. Its signed purpose is intentionally narrower
                    // than an ordinary GameHub session and can never authorize library, tenant,
                    // lifecycle, backup, credential, or role operations.
                    var sessionPurpose = principal?.FindFirstValue(GameHubClaimTypes.SessionPurpose);
                    if (string.Equals(
                            sessionPurpose,
                            GameHubClaimTypes.OidcLinkOnlySessionPurpose,
                            StringComparison.Ordinal)
                        && !MultiUserSessionBoundary.AllowsOidcLinkOnlyRequest(
                            context.HttpContext.Request.Method,
                            context.HttpContext.Request.Path))
                    {
                        context.Fail("This session is restricted to OIDC identity linking.");
                        return;
                    }
                    if (string.Equals(
                            sessionPurpose,
                            GameHubClaimTypes.AccountDeletionOnlySessionPurpose,
                            StringComparison.Ordinal)
                        && !MultiUserSessionBoundary.AllowsAccountDeletionOnlyRequest(
                            context.HttpContext.Request.Method,
                            context.HttpContext.Request.Path))
                    {
                        context.Fail("This session is restricted to account deletion retry.");
                        return;
                    }

                    // Keep a cryptographically valid, unexpired deletion request retryable even
                    // after its account row is gone. AccountDeletionService still checks that the
                    // signed pair cannot select another live account or tenant.
                    if (HttpMethods.IsDelete(context.HttpContext.Request.Method)
                        && context.HttpContext.Request.Path.Equals("/api/auth/account"))
                    {
                        return;
                    }

                    var db = context.HttpContext.RequestServices.GetRequiredService<CoreContext>();
                    var isActive = await db.UserAccounts.AsNoTracking().AnyAsync(
                        account => account.Id == accountId
                            && account.TenantId == tenantId
                            && account.Tenant != null
                            && account.Tenant.DeletionRequestedAtUtc == null,
                        context.HttpContext.RequestAborted);
                    if (!isActive)
                    {
                        context.Fail("The account is unavailable or pending deletion.");
                    }
                }
            };
        });
}

if (multiUserSettings.Mode.IsEnabled)
{
    // In production multi-user mode, plain [Authorize] means a GameHub JWT. The API key remains
    // available only to endpoints that explicitly name the operator scheme. Provisioning still
    // requires a maintenance disable; readiness remains available without letting the legacy
    // default-tenant key bypass user authentication.
    builder.Services.AddAuthorization(options =>
    {
        options.DefaultPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .Build();
    });
}

// --- Database ---
var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";

builder.Services.AddDbContext<CoreContext>(o =>
{
    if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
    {
        var connString = builder.Configuration.GetConnectionString("Postgres");
        if (string.IsNullOrEmpty(connString))
        {
            throw new InvalidOperationException("Postgres connection string is missing.");
        }
        o.UseNpgsql(connString);
    }
    else
    {
        var connString = builder.Configuration.GetConnectionString("Sqlite") ?? "Data Source=core.db";
        o.UseSqlite(connString);
    }

    // --- Multi-Provider Migration Handling ---
    // In a setup with multiple migration folders (sqlite/postgres) in the same project,
    // the model snapshot can fall out of sync with the active provider.
    // We suppress this warning because we apply the correct migrations conditionally.
    o.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

var app = builder.Build();

app.Logger.LogInformation(
    "Guarded multi-user mode is {MultiUserMode}; OIDC is {OidcMode}; OIDC provisioning is {ProvisioningMode}; configuration blockers: {Blockers}",
    multiUserSettings.Mode.IsEnabled ? "enabled" : "disabled",
    oidcSettings.IsEnabled ? "enabled" : "disabled",
    oidcSettings.ProvisioningMode.ToSafeLabel(),
    multiUserSettings.ConfigurationBlockers.Count == 0
        ? "none"
        : string.Join(",", multiUserSettings.ConfigurationBlockers));

// --- Swagger UI ---
app.UseSwagger();
app.UseSwaggerUI();

// --- Database Initialization ---
// Automatically applies any pending EF Core migrations on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
    // Note: In a production multi-provider setup, you would typically 
    // have separate migration assemblies. Here we apply them based on 
    // the active provider to avoid conflicts between SQLite and Postgres.
    db.Database.Migrate();
    // Database readiness is checked after migrations so an upgraded installation remains
    // startable with the switch off, but an enabled deployment cannot start with unlinked live
    // accounts. This validation is read-only and never provisions, links, deletes, or rewrites.
    var multiUserEnablement = scope.ServiceProvider.GetRequiredService<MultiUserEnablementService>();
    await multiUserEnablement.ValidateStartupAsync();
}

// --- Error Handling ---
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// --- Health Check Endpoint ---
app.MapHealthChecks("/health");

// --- Authentication & Authorization ---
// Rate limiting must run before endpoint execution. Authentication then validates the
// selected credential and creates HttpContext.User; authorization evaluates [Authorize]
// metadata against that principal before MapControllers invokes an action.
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.Run();
