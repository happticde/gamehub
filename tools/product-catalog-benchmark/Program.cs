using System.Text.Json;
using System.Text.Json.Serialization;
using GameHub.Core.Services;

var parsedArguments = ParseArguments(args);
if (!parsedArguments.TryGetValue("dataset", out var datasetPath))
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project tools/product-catalog-benchmark -- --dataset <path> [--output <path>]");
    return 64;
}
datasetPath = ResolveInputPath(datasetPath);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPhysicalProductCatalog(builder.Configuration);
var app = builder.Build();

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
};

PhysicalProductCatalogBenchmarkDataset dataset;
try
{
    var json = await File.ReadAllTextAsync(datasetPath);
    dataset = JsonSerializer.Deserialize<PhysicalProductCatalogBenchmarkDataset>(json, jsonOptions)
        ?? throw new InvalidDataException("The benchmark dataset is empty.");
}
catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
{
    Console.Error.WriteLine($"Benchmark dataset could not be loaded: {exception.GetType().Name}.");
    return 65;
}

var runner = app.Services.GetRequiredService<PhysicalProductCatalogBenchmarkRunner>();
PhysicalProductCatalogBenchmarkReport report;
try
{
    report = await runner.RunAsync(dataset);
}
catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine($"Benchmark dataset was rejected: {exception.GetType().Name}.");
    return 65;
}

var reportJson = JsonSerializer.Serialize(report, jsonOptions);
if (parsedArguments.TryGetValue("output", out var outputPath))
{
    await File.WriteAllTextAsync(outputPath, reportJson + Environment.NewLine);
    Console.WriteLine("Benchmark report written. Raw GTINs and provider payloads were omitted.");
}
else
{
    Console.WriteLine(reportJson);
}

if (string.Equals(report.Provider, PhysicalProductCatalogSettings.DisabledProvider, StringComparison.Ordinal))
{
    Console.Error.WriteLine(
        "No product catalogue provider is configured; this report is a harness check, not a live benchmark.");
    return 2;
}

return 0;

static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index++)
    {
        var argument = arguments[index];
        if (argument is not ("--dataset" or "--output") || index + 1 >= arguments.Length)
        {
            continue;
        }

        parsed[argument[2..]] = arguments[++index];
    }

    return parsed;
}

static string ResolveInputPath(string path)
{
    if (Path.IsPathRooted(path))
    {
        return path;
    }

    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
         directory is not null;
         directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, path);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        if (File.Exists(Path.Combine(directory.FullName, "GameHub.slnx")))
        {
            return candidate;
        }
    }

    return Path.GetFullPath(path);
}
