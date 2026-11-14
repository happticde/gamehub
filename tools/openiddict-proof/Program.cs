using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using GameHub.OpenIddictProof;

await using var host = new ProofHost();
await host.StartAsync();
var checks = 0;

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}

async Task<JsonObject> Post(string path, IEnumerable<KeyValuePair<string, string>> fields, string? bearer = null)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
    if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    using var response = await host.Client.SendAsync(request);
    var content = await response.Content.ReadAsStringAsync();
    // Never include protocol bodies in exceptions/output, even for a failed check.
    var result = string.IsNullOrEmpty(content) ? new JsonObject() : JsonNode.Parse(content)!.AsObject();
    result["http_status"] = (int)response.StatusCode;
    return result;
}

static string Field(JsonObject response, string name) => response[name]?.GetValue<string>()
    ?? throw new InvalidOperationException("Protocol response missing expected field: " + name);

List<KeyValuePair<string, string>> DeviceFields(params string[] resources) =>
    new List<KeyValuePair<string, string>>
    {
        new("client_id", ProofHost.ClientId), new("scope", "library:read offline_access")
    }.Concat(resources.Select(r => new KeyValuePair<string, string>("resource", r))).ToList();

List<KeyValuePair<string, string>> TokenFields(string token, bool refresh = false, string[]? resources = null,
    string? scope = null)
{
    var fields = new List<KeyValuePair<string, string>>
    {
        new("client_id", ProofHost.ClientId),
        new("grant_type", refresh ? "refresh_token" : "urn:ietf:params:oauth:grant-type:device_code"),
        new(refresh ? "refresh_token" : "device_code", token)
    };
    fields.AddRange((resources ?? [ProofHost.Resource]).Select(r => new KeyValuePair<string, string>("resource", r)));
    if (scope is not null) fields.Add(new("scope", scope));
    return fields;
}

async Task ExpectError(string path, IEnumerable<KeyValuePair<string, string>> fields, string error, string name)
{
    var result = await Post(path, fields);
    var actual = result["error"]?.GetValue<string>();
    Check(actual == error && result["http_status"]!.GetValue<int>() == 400
        && !result.ContainsKey("access_token") && !result.ContainsKey("refresh_token"),
        name + " (expected " + error + ", received " + (actual ?? "no protocol error") + ")");
}

async Task<HttpStatusCode> Access(string path, string? token = null)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, path);
    if (token is not null) request.Headers.Authorization = new("Bearer", token);
    using var response = await host.Client.SendAsync(request);
    return response.StatusCode;
}

async Task<JsonObject> Inspect(string token)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "/integration");
    request.Headers.Authorization = new("Bearer", token);
    using var response = await host.Client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
}

void CheckPermissions(JsonObject principal, string name)
{
    Check(principal["audiences"]!.AsArray().Select(v => v!.GetValue<string>()).SequenceEqual([ProofHost.Resource]),
        name + " has exactly the approved audience");
    var scopes = principal["scopes"]!.AsArray().Select(v => v!.GetValue<string>()).ToHashSet();
    Check(scopes.SetEquals(["library:read", "offline_access"]), name + " preserves read-only consent");
}

async Task<JsonObject> NewGrant()
{
    var device = await Post("/connect/device", DeviceFields(ProofHost.Resource));
    var approval = await Post("/connect/verify", [new("user_code", Field(device, "user_code"))], host.BrowserToken());
    Check(approval["http_status"]!.GetValue<int>() == 200 && !approval.ContainsKey("error"), "explicit browser approval completes");
    var result = await Post("/connect/token", TokenFields(Field(device, "device_code")));
    Check(result.ContainsKey("access_token") && result.ContainsKey("refresh_token"), "approved grant issues both credentials");
    return result;
}

await ExpectError("/connect/device", DeviceFields(), "invalid_target", "device request requires a resource");
await ExpectError("/connect/device", DeviceFields("https://other.example/api"), "invalid_target", "unknown device resource rejected");
await ExpectError("/connect/device", DeviceFields(ProofHost.RemoteResource), "invalid_target", "registered remote resource rejected for stdio client");
await ExpectError("/connect/device", DeviceFields(ProofHost.Resource, ProofHost.RemoteResource), "invalid_target", "multiple device resources rejected");
await ExpectError("/connect/device", DeviceFields(ProofHost.Resource, ProofHost.Resource), "invalid_target", "duplicate device resource rejected");

var pending = await Post("/connect/device", DeviceFields(ProofHost.Resource));
var deviceCode = Field(pending, "device_code");
await ExpectError("/connect/token", TokenFields(deviceCode), "authorization_pending", "pending device cannot issue tokens");
var anonymousApproval = await Post("/connect/verify", [new("user_code", Field(pending, "user_code"))]);
Check(anonymousApproval["http_status"]?.GetValue<int>() == 401, "approval requires separate browser authentication");

// Use a separate request so pending polling does not affect one-time redemption evidence.
var single = await Post("/connect/device", DeviceFields(ProofHost.Resource));
await Post("/connect/verify", [new("user_code", Field(single, "user_code"))], host.BrowserToken());
await ExpectError("/connect/token", TokenFields(Field(single, "device_code"), resources: []), "invalid_target", "device redemption requires resource");
await ExpectError("/connect/token", TokenFields(Field(single, "device_code"), resources: [ProofHost.RemoteResource]), "invalid_target", "device redemption cannot substitute resource");
await ExpectError("/connect/token", TokenFields(Field(single, "device_code"), resources: [ProofHost.Resource, ProofHost.RemoteResource]), "invalid_target", "device redemption rejects multiple resources");
var redeemed = await Post("/connect/token", TokenFields(Field(single, "device_code")));
Check(redeemed.ContainsKey("access_token"), "device redeemed after explicit approval");
await ExpectError("/connect/token", TokenFields(Field(single, "device_code")), "invalid_grant", "device code cannot be redeemed twice");
Check(await Access("/integration", Field(redeemed, "access_token")) == HttpStatusCode.Unauthorized,
    "device-code replay revokes its issued access token");

var grant = await NewGrant();
var access = Field(grant, "access_token");
var refresh = Field(grant, "refresh_token");
var initialPrincipal = await Inspect(access);
CheckPermissions(initialPrincipal, "initial access");
Check(await Access("/integration", access) == HttpStatusCode.OK, "integration credential authenticates integration route");
Check(await Access("/browser", host.BrowserToken()) == HttpStatusCode.OK, "browser bearer authenticates browser route");
Check(await Access("/integration", host.BrowserToken()) == HttpStatusCode.Unauthorized, "browser bearer cannot substitute for integration token");
Check(await Access("/browser", access) == HttpStatusCode.Unauthorized, "integration token cannot substitute for browser bearer");
Check(await Access("/integration", host.BrowserToken(ProofHost.Resource)) == HttpStatusCode.Unauthorized, "browser token with integration audience still rejected");
Check(await Access("/integration", refresh) == HttpStatusCode.Unauthorized, "refresh credential cannot be used as an access token");
Check(await Access("/integration") == HttpStatusCode.Unauthorized, "anonymous integration access rejected");

await ExpectError("/connect/token", TokenFields(refresh, true, []), "invalid_target", "refresh requires resource");
await ExpectError("/connect/token", TokenFields(refresh, true, ["https://other.example/api"]), "invalid_target", "refresh rejects unknown resource");
await ExpectError("/connect/token", TokenFields(refresh, true, [ProofHost.RemoteResource]), "invalid_target", "refresh cannot switch to registered remote resource");
await ExpectError("/connect/token", TokenFields(refresh, true, [ProofHost.Resource, ProofHost.RemoteResource]), "invalid_target", "refresh cannot widen resource set");
await ExpectError("/connect/token", TokenFields(refresh, true, scope: "library:read tracking:write offline_access"), "invalid_grant", "refresh cannot widen to registered write scope");

var rotated = await Post("/connect/token", TokenFields(refresh, true));
var successorRefresh = Field(rotated, "refresh_token");
var successorAccess = Field(rotated, "access_token");
var successorPrincipal = await Inspect(successorAccess);
CheckPermissions(successorPrincipal, "successor access");
Check(initialPrincipal["authorizationId"]!.GetValue<string>() == successorPrincipal["authorizationId"]!.GetValue<string>(),
    "refresh keeps the same protocol authorization");
Check(successorRefresh != refresh, "refresh rotates credential");
Check(await Access("/integration", successorAccess) == HttpStatusCode.OK, "successor access works before reuse");
await ExpectError("/connect/token", TokenFields(refresh, true), "invalid_grant", "confirmed refresh reuse rejected without grace");
Check(await Access("/integration", successorAccess) == HttpStatusCode.Unauthorized, "confirmed reuse invalidates successor access immediately");
await ExpectError("/connect/token", TokenFields(successorRefresh, true), "invalid_grant", "confirmed reuse invalidates successor refresh");
Check(await Access("/integration", access) == HttpStatusCode.Unauthorized, "confirmed reuse invalidates original access");
Check(await host.AuthorizationIsValidAsync(initialPrincipal["authorizationId"]!.GetValue<string>()),
    "reuse leaves authorization row valid (GameHub family policy still required)");

var independent = await NewGrant();
Check(await Access("/integration", Field(independent, "access_token")) == HttpStatusCode.OK, "independent authorization survives another grant reuse");
await host.RevokeAsync(Field(independent, "access_token"), authorization: false);
Check(await Access("/integration", Field(independent, "access_token")) == HttpStatusCode.Unauthorized, "token-entry revocation immediate on next request");
var afterTokenRevocation = await Post("/connect/token", TokenFields(Field(independent, "refresh_token"), true));
Check(afterTokenRevocation.ContainsKey("access_token"), "access-token-only revocation leaves refresh valid (policy boundary)");
var survivingAccess = Field(afterTokenRevocation, "access_token");

var authorizationGrant = await NewGrant();
await host.RevokeAsync(Field(authorizationGrant, "access_token"), authorization: true);
Check(await Access("/integration", Field(authorizationGrant, "access_token")) == HttpStatusCode.Unauthorized, "authorization-entry revocation immediate on next request");
await ExpectError("/connect/token", TokenFields(Field(authorizationGrant, "refresh_token"), true), "invalid_grant", "authorization revocation blocks refresh");

var protocolGrant = await NewGrant();
var revoked = await Post("/connect/revoke", [new("client_id", ProofHost.ClientId),
    new("token", Field(protocolGrant, "refresh_token")), new("token_type_hint", "refresh_token")]);
Check(revoked["http_status"]!.GetValue<int>() == 200 && !revoked.ContainsKey("error"), "standard revocation endpoint accepts owned refresh token");
await ExpectError("/connect/token", TokenFields(Field(protocolGrant, "refresh_token"), true), "invalid_grant", "protocol-revoked refresh cannot renew");
Check(await Access("/integration", Field(protocolGrant, "access_token")) == HttpStatusCode.OK,
    "protocol refresh revocation alone leaves access valid (GameHub logout policy required)");
Check(await Access("/integration", survivingAccess) == HttpStatusCode.OK, "revocation preserves unrelated authorization");
Check(await Access("/browser", host.BrowserToken()) == HttpStatusCode.OK, "integration revocations preserve browser bearer");
Console.WriteLine($"Completed {checks} protocol checks. No production readiness or real-client interoperability claimed.");
