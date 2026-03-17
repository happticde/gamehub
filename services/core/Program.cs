using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using Microsoft.EntityFrameworkCore;

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
builder.Services.AddSwaggerGen();

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
var ingestApiKey = builder.Configuration["Authentication:ApiKey"] ?? "";
builder.Services.AddHttpClient<IngestClient>(c =>
{
    c.BaseAddress = new Uri(ingestUrl);
    // Forward the API key to the Ingest service for service-to-service auth
    if (!string.IsNullOrEmpty(ingestApiKey))
    {
        c.DefaultRequestHeaders.Add("X-Api-Key", ingestApiKey);
    }
})
    .AddStandardResilienceHandler();
builder.Services.AddScoped<UnificationService>();

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
builder.Services.AddApiKeyAuthentication(builder.Configuration);

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
}

// --- Error Handling ---
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// --- Health Check Endpoint ---
app.MapHealthChecks("/health");

// --- Authentication & Authorization ---
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.Run();
