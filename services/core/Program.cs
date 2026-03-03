using GameHub.Core.Data;
using GameHub.Core.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register the IngestClient with a typed HttpClient pointing at the Ingest service.
// AddStandardResilienceHandler() adds automatic:
//   - Retries with exponential backoff (default: 3 attempts)
//   - Circuit breaker (stops calling a failing service to let it recover)
//   - Request timeout (prevents hanging forever on slow responses)
//   - Total request timeout (caps the total time including all retries)
var ingestUrl = builder.Configuration["Services:IngestUrl"] ?? "http://localhost:5000";
builder.Services.AddHttpClient<IngestClient>(c => c.BaseAddress = new Uri(ingestUrl))
    .AddStandardResilienceHandler();
builder.Services.AddScoped<UnificationService>();

var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=core.db";

builder.Services.AddDbContext<CoreContext>(o =>
{
    if (provider == "Postgres") o.UseNpgsql(connString);
    else o.UseSqlite(connString);
});

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<CoreContext>().Database.EnsureCreated();
}

app.MapControllers();
app.Run();
