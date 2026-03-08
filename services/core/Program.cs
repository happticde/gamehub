using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared;
using GameHub.Shared.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- MVC & Swagger ---
builder.Services.AddControllers();
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
// IgdbClient is registered as both the concrete type (for HttpClient injection)
// and the IGameMetadataProvider interface (for EnrichmentService injection).
// To swap providers, replace IgdbClient with another IGameMetadataProvider implementation.
builder.Services.AddHttpClient<IgdbClient>()
    .AddStandardResilienceHandler();
builder.Services.AddScoped<IGameMetadataProvider>(sp => sp.GetRequiredService<IgdbClient>());
builder.Services.AddScoped<EnrichmentService>();

// --- Background Enrichment ---
if (builder.Configuration.GetValue("Igdb:EnableBackgroundEnrichment", true))
{
    builder.Services.AddHostedService<EnrichmentBackgroundService>();
}

// --- Authentication (ADR-002) ---
builder.Services.AddApiKeyAuthentication(builder.Configuration);

// --- Database ---
var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=core.db";

builder.Services.AddDbContext<CoreContext>(o =>
{
    if (provider == "Postgres") o.UseNpgsql(connString);
    else o.UseSqlite(connString);
});

var app = builder.Build();

// --- Swagger UI ---
app.UseSwagger();
app.UseSwaggerUI();

// --- Database Initialization ---
// Automatically applies any pending EF Core migrations on startup.
// To add a new migration after changing entities:
//   dotnet ef migrations add <Name> --project services/core/GameHub.Core.csproj
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
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
