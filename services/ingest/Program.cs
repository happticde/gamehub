using GameHub.Ingest.Data;
using GameHub.Ingest.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- MVC & JSON Options ---
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        // Don't include null properties in API responses — keeps JSON cleaner
        options.JsonSerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// --- Health Checks ---
builder.Services.AddHealthChecks()
    .AddDbContextCheck<IngestContext>("database");

// --- Platform Fetchers (ADR-001: Server-Side Retrieval) ---
// These services call external APIs (Steam, GOG, PSN, Epic) to fetch game libraries.
// Used by RetrieveController for one-call fetch + ingest.
// Steam's API key is a query parameter, so remove the framework's URI loggers from this
// client. Epic's OAuth requests contain Authorization headers, so use the same no-framework-URI
// logging boundary there. RetrieveController also returns only sanitized upstream errors.
builder.Services.AddHttpClient<ISteamFetcher, SteamFetcher>().RemoveAllLoggers();
builder.Services.AddTransient<IGogFetcher, GogFetcher>();
builder.Services.AddTransient<IPsnFetcher, PsnFetcher>();
builder.Services.AddHttpClient<EpicGamesClient>().RemoveAllLoggers();
builder.Services.AddTransient<IEpicAuthenticationClient>(
    provider => provider.GetRequiredService<EpicGamesClient>());
builder.Services.AddTransient<IEpicFetcher>(
    provider => provider.GetRequiredService<EpicGamesClient>());

// --- Authentication (ADR-002) ---
// External callers use the compatibility API key, which always maps to one server-configured
// tenant. Core uses a separate internal key plus tenant header. Selecting the delegation
// scheme whenever either internal header is present makes spoofed partial requests fail closed.
builder.Services.AddApiKeyAuthentication(builder.Configuration);
const string ingestAuthenticationScheme = "GameHubIngest";
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = ingestAuthenticationScheme;
        options.DefaultChallengeScheme = ingestAuthenticationScheme;
    })
    .AddPolicyScheme(ingestAuthenticationScheme, null, options =>
    {
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.ContainsKey(TenantDelegationAuthenticationHandler.ServiceKeyHeaderName) ||
            context.Request.Headers.ContainsKey(TenantDelegationAuthenticationHandler.TenantHeaderName)
                ? TenantDelegationAuthenticationHandler.SchemeName
                : ApiKeyAuthenticationHandler.SchemeName;
    })
    .AddScheme<AuthenticationSchemeOptions, TenantDelegationAuthenticationHandler>(
        TenantDelegationAuthenticationHandler.SchemeName,
        null);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IIngestTenantContext, AuthenticatedIngestTenantContext>();

// --- Tenant Platform Credential Vault ---
// Empty key configuration keeps the API running but makes credential writes fail closed with
// HTTP 503. Invalid or incomplete key-ring configuration fails during startup.
var platformCredentialKeyRing = PlatformCredentialKeyRing.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(platformCredentialKeyRing);
builder.Services.AddSingleton<PlatformCredentialProtector>();
builder.Services.AddScoped<PlatformCredentialService>();
builder.Services.AddScoped<TenantDataDeletionService>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

// Ingest independently reads the immutable operator switch. When enabled, internal tenant
// delegation and a usable credential-vault key are mandatory; direct compatibility API-key
// access is removed from the default policy so it cannot select the default tenant in production.
var multiUserMode = MultiUserMode.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(multiUserMode);
if (multiUserMode.IsEnabled)
{
    var serviceKey = builder.Configuration["Authentication:ServiceKey"]?.Trim() ?? "";
    if (serviceKey.Length is < 32 or > 512
        || serviceKey.Any(character => character is < '!' or > '~'))
    {
        throw new InvalidOperationException(
            "MultiUser:Enabled requires a 32-512 character internal tenant-delegation service key.");
    }
    if (!platformCredentialKeyRing.IsConfigured)
    {
        throw new InvalidOperationException(
            "MultiUser:Enabled requires a configured platform credential encryption key.");
    }

    builder.Services.AddAuthorization(options =>
    {
        options.DefaultPolicy = new AuthorizationPolicyBuilder(
                TenantDelegationAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .Build();
    });
}

// --- Database ---
var dbProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";
builder.Services.AddDbContext<IngestContext>(options =>
{
    if (dbProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
    {
        var connectionString = builder.Configuration.GetConnectionString("Postgres");
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException("Postgres connection string is missing.");
        }
        options.UseNpgsql(connectionString);
    }
    else
    {
        var connectionString = builder.Configuration.GetConnectionString("Sqlite") ??
            "Data Source=/app/data/Gamehub.db";
        options.UseSqlite(connectionString);
    }
});

var app = builder.Build();

app.Logger.LogInformation(
    "Guarded multi-user mode is {MultiUserMode}; direct compatibility API-key access is {CompatibilityAccess}",
    multiUserMode.IsEnabled ? "enabled" : "disabled",
    multiUserMode.IsEnabled ? "disabled" : "enabled");

// --- Database Initialization ---
// Automatically applies any pending EF Core migrations on startup.
// To add a new migration after changing entities:
//   dotnet ef migrations add <Name> --project services/ingest/GameHub.Ingest.csproj
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IngestContext>();
    db.Database.Migrate();
}

// --- Error Handling ---
// In development, show full stack traces in error responses instead of bare 500s.
// In production, this is automatically disabled for security.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// --- Swagger UI ---
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "GameHub Ingest API v1");
    c.RoutePrefix = string.Empty; // Serve Swagger UI at the root URL
});

// --- Health Check Endpoint ---
app.MapHealthChecks("/health");

// --- Authentication & Authorization ---
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.Run();
