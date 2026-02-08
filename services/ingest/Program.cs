using GameHub.Ingest.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

// Auto-create DB
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IngestContext>();
    db.Database.EnsureCreated();
}

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "GameHub API v1");
    c.RoutePrefix = string.Empty;
});

app.MapControllers();
app.Run();
