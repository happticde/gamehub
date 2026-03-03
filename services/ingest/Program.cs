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

// --- Database Initialization ---
// EnsureCreated() creates the database and all tables if they don't exist.
// To switch to migrations later:
//   1. Run: dotnet ef migrations add InitialCreate
//   2. Replace the line below with: db.Database.Migrate();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IngestContext>();
    db.Database.EnsureCreated();
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
