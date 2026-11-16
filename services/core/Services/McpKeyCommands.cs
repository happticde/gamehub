using System.Text.Json;

namespace GameHub.Core.Services;

/// <summary>Offline administrative entry point; deliberately precedes application/configuration startup.</summary>
public static class McpKeyCommands
{
    /// <summary>Runs provisioning/check/renewal using dedicated files; emits no exceptions or secrets.</summary>
    public static int Run(string[] args, TextWriter output)
    {
        try
        {
            if (args.Length == 0 || args[0] is not ("setup" or "check" or "renew")) throw new McpKeyStoreException("invalid_command");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var flags = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Length; index++)
            {
                var option = args[index];
                if (option is "--enable" or "--force")
                { if (!flags.Add(option)) throw new McpKeyStoreException("invalid_command"); }
                else if (option is "--core-directory" or "--frontend-directory" or "--public-origin" or "--adopt-existing")
                {
                    if (++index >= args.Length || !values.TryAdd(option, args[index])) throw new McpKeyStoreException("invalid_command");
                }
                else throw new McpKeyStoreException("invalid_command");
            }
            var store = new McpManagedKeyStore(values.GetValueOrDefault("--core-directory", "/var/lib/gamehub-mcp"));
            if (args[0] == "setup")
            {
                if (!OperatingSystem.IsLinux() || McpPrivateFiles.GetEffectiveUser() != 0)
                    throw new McpKeyStoreException("setup_requires_root");
                if (flags.Contains("--force")) throw new McpKeyStoreException("invalid_command");
                var core = values.GetValueOrDefault("--core-directory", "/var/lib/gamehub-mcp");
                var frontend = values.GetValueOrDefault("--frontend-directory", "/var/lib/gamehub-mcp-frontend");
                McpPrivateFiles.PrepareRoot(core);
                McpPrivateFiles.PrepareRoot(frontend);
                McpLegacyKeyImport? legacy = null;
                if (values.TryGetValue("--adopt-existing", out var descriptor))
                    legacy = JsonSerializer.Deserialize<McpLegacyKeyImport>(McpPrivateFiles.Read(descriptor, 16384))
                        ?? throw new McpKeyStoreException("invalid_import");
                store.Setup(frontend, values.GetValueOrDefault("--public-origin", ""), flags.Contains("--enable"), legacy);
                // Match the two existing production images, not the bootstrap user's identity.
                McpPrivateFiles.AssignOwner(core, 1654);
                McpPrivateFiles.AssignOwner(frontend, 1001);
            }
            else
            {
                if (values.Keys.Any(key => key != "--core-directory") || flags.Contains("--enable")
                    || (args[0] == "check" && flags.Contains("--force"))) throw new McpKeyStoreException("invalid_command");
                if (args[0] == "renew") store.Renew(flags.Contains("--force"));
            }
            var readiness = store.Check();
            output.WriteLine(JsonSerializer.Serialize(readiness));
            return readiness.Code == "ready" ? 0 : 2;
        }
        catch (Exception exception)
        {
            output.WriteLine(JsonSerializer.Serialize(new { Code = exception is McpKeyStoreException error ? error.Code : "invalid_storage" }));
            return 1;
        }
    }
}
