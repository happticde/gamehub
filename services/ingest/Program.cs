using GameHub.Ingest.Data;
using GameHub.Ingest.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- MVC & JSON Options ---
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
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
// These services call external APIs (Steam, GOG, PSN) to fetch game libraries.
// Used by RetrieveController for one-call fetch + ingest.
builder.Services.AddHttpClient<SteamFetcher>(); // Uses typed HttpClient
builder.Services.AddTransient<GogFetcher>();     // Creates its own cookie-based client
builder.Services.AddTransient<PsnFetcher>();     // Creates its own cookie-based client

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

// --- Database Initialization ---
// Automatically applies any pending EF Core migrations on startup.
// To add a new migration after changing entities:
//   dotnet ef migrations add <Name> --project services/ingest/GameHub.Ingest.csproj
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IngestContext>();
    db.Database.Migrate();
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

app.MapControllers();
app.Run();
