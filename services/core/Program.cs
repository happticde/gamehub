using GameHub.Core.Data;
using GameHub.Core.Services;
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
builder.Services.AddHttpClient<IngestClient>(c => c.BaseAddress = new Uri(ingestUrl))
    .AddStandardResilienceHandler();
builder.Services.AddScoped<UnificationService>();

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



// --- Database Migration (Improvement #7) ---
// On startup, automatically apply any pending EF Core migrations.
// This replaces the old EnsureCreated() which couldn't handle schema changes.
//
// To create a new migration after changing entities, run inside the devcontainer:
//   cd services/core
//   dotnet ef migrations add <MigrationName>
//
// Migrations are applied automatically on next app startup.
// For a fresh database, the initial migration creates all tables from scratch.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CoreContext>();
    db.Database.Migrate();
}

// --- Health Check Endpoint ---
app.MapHealthChecks("/health");

app.MapControllers();
app.Run();
