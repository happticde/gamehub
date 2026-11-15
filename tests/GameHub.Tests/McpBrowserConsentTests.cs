using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Tests;

public sealed partial class McpDeviceProtocolTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BrowserConsent_ProductionEndpoints_ApproveNarrowedScopesAndRedeem(bool postgres, bool write)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, productionConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        var fields = DeviceHost.Fields();
        fields.RemoveAll(field => field.Key == Parameters.Scope);
        fields.Add(new(Parameters.Scope, "library:read tracking:write offline_access"));
        var device = await host.PostAsync(McpDeviceProtocol.DevicePath, fields);
        Assert.Equal(DeviceHost.Issuer + "mcp/verify", device.GetProperty("verification_uri").GetString());
        Assert.False(device.TryGetProperty("verification_uri_complete", out _));
        var preview = await host.PreviewAsync(device, browser);
        Assert.True(preview.GetProperty("trackingWriteRequested").GetBoolean());
        Assert.Equal(DeviceHost.Resource, preview.GetProperty("resource").GetString());
        using var decision = await host.BrowserDecisionAsync(device, browser, preview, write: write);
        Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
        Assert.Empty(await decision.Content.ReadAsStringAsync());
        var issued = await host.PollAsync(device);
        Assert.True(issued.TryGetProperty("access_token", out _), issued.ToString());
        var access = await host.AccessAsync(issued);
        await host.AssertConsentOwnerAsync(access, write);
        using var replay = await host.BrowserDecisionAsync(device, browser, preview, write: write);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Empty(await replay.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserConsent_Deny_ConsumesWithoutGrantOrCredential(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, productionConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        var device = await host.DeviceAsync();
        var preview = await host.PreviewAsync(device, browser);
        using var denied = await host.BrowserDecisionAsync(device, browser, preview, action: "deny");
        Assert.Equal(HttpStatusCode.NoContent, denied.StatusCode);
        Assert.Equal(Errors.AccessDenied, (await host.PollAsync(device)).GetProperty("error").GetString());
        await host.WithDbAsync(async db => Assert.Empty(await db.McpGrants.ToListAsync()));
    }

    [Theory]
    [InlineData(false, "account")]
    [InlineData(true, "account")]
    [InlineData(false, "code")]
    [InlineData(true, "code")]
    [InlineData(false, "tampered")]
    [InlineData(true, "tampered")]
    [InlineData(false, "expired")]
    [InlineData(true, "expired")]
    [InlineData(false, "expired-during")]
    [InlineData(true, "expired-during")]
    [InlineData(false, "widened")]
    [InlineData(true, "widened")]
    public async Task BrowserConsent_ChangedOrStaleForm_NeverCommits(bool postgres, string defect)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, productionConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        var device = await host.DeviceAsync();
        var preview = await host.PreviewAsync(device, browser);
        if (defect == "account") { host.SwitchConsentOwner(); browser = await host.SeedConsentBrowserAsync(); }
        if (defect == "code") device = await host.DeviceAsync();
        if (defect == "expired") host.Clock.Advance = TimeSpan.FromMinutes(6);
        if (defect == "expired-during") host.AfterConsentAdmission = () => { host.Clock.Advance = TimeSpan.FromMinutes(6); return Task.CompletedTask; };
        using var decision = await host.BrowserDecisionAsync(device, browser, preview,
            write: defect == "widened", tamper: defect == "tampered");
        Assert.Equal(HttpStatusCode.BadRequest, decision.StatusCode);
        Assert.Empty(await decision.Content.ReadAsStringAsync());
        await host.WithDbAsync(async db => Assert.Empty(await db.McpGrants.ToListAsync()));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("link")]
    [InlineData("deletion-token")]
    [InlineData("foreign-tenant")]
    [InlineData("foreign-family")]
    [InlineData("corrupt")]
    [InlineData("missing")]
    public async Task BrowserPreview_UnverifiedBrowser_NoDisplayOrApproval(string defect)
    {
        await using var host = await DeviceHost.CreateAsync(productionConsent: true);
        var browser = await host.SeedConsentBrowserAsync(defect);
        var device = await host.DeviceAsync();
        using var response = await host.BrowserRequestAsync(device, browser, "preview");
        Assert.False(response.IsSuccessStatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("service")]
    [InlineData("origin")]
    [InlineData("query")]
    [InlineData("get")]
    [InlineData("duplicate")]
    [InlineData("owner")]
    [InlineData("oversized")]
    public async Task BrowserPreview_ForgedTransport_IsRejectedBeforeDisplay(string defect)
    {
        await using var host = await DeviceHost.CreateAsync(productionConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        var device = await host.DeviceAsync();
        using var request = new HttpRequestMessage(defect == "get" ? HttpMethod.Get : HttpMethod.Post,
            McpDeviceProtocol.VerifyPath + (defect == "query" ? "?user_code=do-not-reflect" : ""));
        var fields = new List<KeyValuePair<string, string>> { new("user_code", device.GetProperty("user_code").GetString()!), new("action", "preview") };
        if (defect == "duplicate") fields.Add(new("user_code", "duplicate-secret"));
        if (defect == "owner") fields.Add(new("tenant_id", Guid.NewGuid().ToString()));
        if (defect == "oversized") fields.Add(new("confirmation", new string('x', 5000)));
        request.Content = new FormUrlEncodedContent(fields);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", browser);
        if (defect != "service") request.Headers.Add(McpDeviceSettings.BffHeader, DeviceHost.ConsentServiceKey);
        if (defect == "origin") request.Headers.Add("Origin", DeviceHost.Issuer.TrimEnd('/'));
        using var response = await host.Client.SendAsync(request);
        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(device.GetProperty("user_code").GetString()!, body);
        Assert.DoesNotContain("do-not-reflect", body);
        Assert.DoesNotContain("confirmation", body);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task BrowserPreview_KeyRingRestart_PreservesBoundDecision()
    {
        await using var host = await DeviceHost.CreateAsync(productionConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        var device = await host.DeviceAsync();
        var preview = await host.PreviewAsync(device, browser);
        await host.RestartAsync();
        using var decision = await host.BrowserDecisionAsync(device, browser, preview);
        Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ConsentRaceCases))]
    public async Task BrowserConsent_ConcurrentLifecycleChange_OrdersBoundFormAndRevocation(bool postgres, bool approvalWins, string change)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, productionConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        var device = await host.DeviceAsync();
        var preview = await host.PreviewAsync(device, browser);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task> pause = async () => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(20)); };
        if (approvalWins) host.AfterConsentAdmission = pause; else host.BeforeConsentRequest = pause;
        var decision = Task.Run(() => host.BrowserDecisionAsync(device, browser, preview));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            if (approvalWins)
            {
                host.LifecycleStarting = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var revoke = Task.Run(() => change == "browser" ? host.LocalLogoutAsync() : host.RevokeAsync(change));
                await host.LifecycleStarting.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await Assert.ThrowsAsync<TimeoutException>(() => revoke.WaitAsync(TimeSpan.FromMilliseconds(150)));
                release.TrySetResult();
                using var response = await decision.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                await revoke.WaitAsync(TimeSpan.FromSeconds(30));
            }
            else
            {
                if (change == "browser") await host.LocalLogoutAsync(); else await host.RevokeAsync(change);
                release.TrySetResult();
                using var response = await decision.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
            }
        }
        finally { release.TrySetResult(); }
        var issued = await host.PollAsync(device);
        Assert.Equal(approvalWins && change == "browser", issued.TryGetProperty("access_token", out _));
        if (approvalWins && change == "browser")
        {
            await host.RevokeAsync("provider");
            Assert.Equal(HttpStatusCode.Unauthorized, await host.AccessStatusAsync(issued));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserConsent_ApproveRacesDeny_OnlyOneDisplayedDecisionCommits(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, productionConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        var device = await host.DeviceAsync();
        var preview = await host.PreviewAsync(device, browser);
        var responses = await Task.WhenAll(host.BrowserDecisionAsync(device, browser, preview),
            host.BrowserDecisionAsync(device, browser, preview, action: "deny"));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
            await host.WithDbAsync(async db => Assert.Equal(responses[0].StatusCode == HttpStatusCode.NoContent ? 1 : 0, await db.McpGrants.CountAsync()));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    private sealed partial class DeviceHost
    {
        internal const string ConsentServiceKey = "fixture-only-mcp-service-key-0123456789";

        internal async Task<JsonElement> PreviewAsync(JsonElement device, string? browser)
        {
            using var response = await BrowserRequestAsync(device, browser, "preview");
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("access_token", body);
            Assert.DoesNotContain("refresh_token", body);
            Assert.DoesNotContain(ConsentServiceKey, body);
            Assert.DoesNotContain(device.GetProperty("user_code").GetString()!, body);
            using var json = JsonDocument.Parse(body);
            return json.RootElement.Clone();
        }

        internal Task<HttpResponseMessage> BrowserDecisionAsync(JsonElement device, string? browser, JsonElement preview,
            string action = "approve", bool write = false, bool tamper = false) => BrowserRequestAsync(device, browser, action,
                (tamper ? "broken" : "") + preview.GetProperty("confirmation").GetString(), write);

        internal async Task<HttpResponseMessage> BrowserRequestAsync(JsonElement device, string? browser, string action,
            string? confirmation = null, bool write = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, McpDeviceProtocol.VerifyPath);
            var fields = new List<KeyValuePair<string, string>> { new("user_code", device.GetProperty("user_code").GetString()!), new("action", action) };
            if (confirmation is not null) { fields.Add(new("confirmation", confirmation)); fields.Add(new("allow_tracking_write", write.ToString().ToLowerInvariant())); }
            request.Content = new FormUrlEncodedContent(fields);
            request.Headers.Add(McpDeviceSettings.BffHeader, ConsentServiceKey);
            if (browser is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", browser);
            var response = await Client.SendAsync(request);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.DoesNotContain(device.GetProperty("user_code").GetString()!, string.Join("", Logs));
            if (browser is not null) Assert.DoesNotContain(browser, string.Join("", Logs));
            return response;
        }
    }
}
