using GameHub.Ingest.Data;
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

// --- Health Checks (Improvement #9) ---
// Registers a /health endpoint that checks DB connectivity.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<IngestContext>("database");

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



// --- Database Migration (Improvement #7) ---
// On startup, automatically apply any pending EF Core migrations.
// This replaces the old EnsureCreated() which couldn't handle schema changes.
//
// To create a new migration after changing entities, run inside the devcontainer:
//   cd services/ingest
//   dotnet ef migrations add <MigrationName>
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
