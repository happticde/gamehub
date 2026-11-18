using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Core.Mcp;

/// <summary>Fetches only the shipped native profiles and caches complete, validated documents.</summary>
internal sealed class McpClientMetadata(HttpClient http, TimeProvider clock) : IDisposable
{
    private const int MaximumBytes = 16 * 1024;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal async Task<string[]?> GetRedirectsAsync(McpRemoteClient client, CancellationToken cancellationToken)
    {
        var entry = _entries.GetOrAdd(client.Id, static _ => new Entry());
        // There are only two keys. A concurrent caller can retry after the in-flight read,
        // instead of waiting in an unbounded queue behind an unreachable external host.
        if (!entry.Gate.Wait(0)) return null;
        try
        {
            if (entry.ValidUntil > clock.GetUtcNow()) return entry.Redirects;
            entry.Redirects = null;
            entry.ValidUntil = clock.GetUtcNow().AddSeconds(30);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, client.Id);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK
                || response.Content.Headers.ContentType?.MediaType != "application/json"
                || response.Content.Headers.ContentLength > MaximumBytes
                || response.Content.Headers.ContentEncoding.Count != 0) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[MaximumBytes + 1];
            var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, deadline.Token);
            if (length > MaximumBytes) return null;
            var redirects = Parse(client, bytes.AsMemory(0, length));
            if (redirects is null) return null;
            var cacheUntil = CacheUntil(response, clock.GetUtcNow());
            entry.Redirects = cacheUntil > clock.GetUtcNow() ? redirects : null;
            entry.ValidUntil = cacheUntil;
            return redirects;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or OperationCanceledException or IOException or JsonException or FormatException)
        {
            return null;
        }
        finally { entry.Gate.Release(); }
    }

    private static string[]? Parse(McpRemoteClient client, ReadOnlyMemory<byte> bytes)
    {
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1)
            || !Text(root, "client_id", client.Id) || !Text(root, "application_type", "native")
            || !Text(root, "token_endpoint_auth_method", "none")
            || root.TryGetProperty("client_secret", out _)
            || !ExactSet(root, "grant_types", [GrantTypes.AuthorizationCode, GrantTypes.RefreshToken])
            || !ExactSet(root, "response_types", [ResponseTypes.Code])
            || (root.TryGetProperty("token_endpoint_auth_methods_supported", out _)
                && !ExactSet(root, "token_endpoint_auth_methods_supported", ["none"]))) return null;

        var redirects = ReadArray(root, "redirect_uris");
        return redirects is { Length: > 0 }
            && redirects.All(redirect => client.RedirectUris.Contains(redirect, StringComparer.Ordinal)) ? redirects : null;
    }

    private static bool Text(JsonElement root, string name, string expected) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() == expected;

    private static bool ExactSet(JsonElement root, string name, string[] expected) =>
        ReadArray(root, name) is { } values && values.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal));

    private static string[]? ReadArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() > 12 || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            return null;
        var values = value.EnumerateArray().Select(item => item.GetString()!).ToArray();
        return values.Distinct(StringComparer.Ordinal).Count() == values.Length ? values : null;
    }

    private static DateTimeOffset CacheUntil(HttpResponseMessage response, DateTimeOffset now)
    {
        var policy = response.Headers.CacheControl;
        if (policy is { NoStore: true } or { NoCache: true }) return now;
        var lifetime = policy?.MaxAge ?? TimeSpan.FromHours(1);
        if (response.Content.Headers.Expires is { } expires)
            lifetime = TimeSpan.FromTicks(Math.Min(lifetime.Ticks, (expires - (response.Headers.Date ?? now)).Ticks));
        var age = response.Headers.Age.GetValueOrDefault();
        if (response.Headers.Date is { } date && now - date > age) age = now - date;
        var bounded = TimeSpan.FromTicks(Math.Clamp(lifetime.Ticks, 0, TimeSpan.TicksPerHour));
        return now + (age >= bounded ? TimeSpan.Zero : bounded - age);
    }

    internal static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        MaxResponseHeadersLength = 16,
        ConnectCallback = ConnectAsync,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var uri = context.InitialRequestMessage.RequestUri;
        var endpoint = context.DnsEndPoint;
        if (uri is null || McpRemoteClient.Find(uri.AbsoluteUri) is null ||
            endpoint.Port != 443 || !string.Equals(uri.IdnHost, endpoint.Host, StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("Unapproved metadata authority.");
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address)))
            throw new HttpRequestException("Metadata authority did not resolve to public addresses.");

        // Dial the checked addresses directly. TLS still verifies the original HTTP authority.
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Metadata authority unavailable.");
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is > 0 and < 224 and not (10 or 127)
                && !(b[0] == 100 && b[1] is >= 64 and <= 127)
                && !(b[0] == 169 && b[1] == 254)
                && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && (b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2)
                    || (b[1] == 88 && b[2] == 99)))
                && !(b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100)))
                && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        return address.ScopeId == 0 && (b[0] & 0xe0) == 0x20
            && !(b[0] == 0x20 && b[1] is 0x01 && (b[2] < 2 || (b[2] == 0x0d && b[3] == 0xb8)))
            && !(b[0] == 0x20 && b[1] == 0x02)
            && !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0);
    }

    public void Dispose()
    {
        http.Dispose();
        foreach (var entry in _entries.Values) entry.Gate.Dispose();
    }

    private sealed class Entry
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal DateTimeOffset ValidUntil { get; set; }
        internal string[]? Redirects { get; set; }
    }
}
