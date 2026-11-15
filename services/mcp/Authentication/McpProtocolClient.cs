using System.Net;
using System.Text.Json;

namespace GameHub.Mcp.Authentication;

/// <summary>Fixed-destination, bounded protocol requests shared by explicit login and renewal.</summary>
internal sealed class McpProtocolClient(McpLoginSettings settings, HttpClient http)
{
    internal async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path,
        List<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        if (path is not ("connect/device" or "connect/token" or "connect/revoke"))
            throw new McpLoginException("Unsupported authorization operation.");
        fields.Add(new("client_id", McpLoginSettings.ClientId));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.Issuer, path))
        { Content = new FormUrlEncodedContent(fields) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new McpLoginException("GameHub redirected a credential request. Configure direct HTTPS protocol routing; redirects are not followed.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var bytes = new byte[8193];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token);
            if (read == 0) break;
            length += read;
        }
        if (length > 8192) throw new McpLoginException("GameHub protocol response exceeded its size limit.");
        if (length == 0 && path == "connect/revoke")
            return (response.StatusCode, JsonSerializer.SerializeToElement(new { }));
        using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.EnumerateObject().Select(property => property.Name).Distinct().Count()
                != document.RootElement.EnumerateObject().Count())
            throw new McpLoginException("GameHub returned an invalid protocol response.");
        return (response.StatusCode, document.RootElement.Clone());
    }

    internal static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    internal static bool Integer(JsonElement value, string name, out int number)
    { number = 0; return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out number); }
}
