using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
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
    public async Task Consent_RealApproval_BindsSignedOwnerAndNarrowsRequestedWrite(bool postgres, bool write)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, realConsent: true);
        host.Epoch = (long)int.MaxValue + 37;
        var browser = await host.SeedConsentBrowserAsync();
        var fields = DeviceHost.Fields();
        fields[1] = new(Parameters.Scope, "library:read tracking:write offline_access");
        var device = await host.PostAsync(McpDeviceProtocol.DevicePath, fields);
        host.ConsentTrackingWrite = write;
        Assert.Equal(HttpStatusCode.NoContent, await host.DecideAsync(device, browser));
        var issued = await host.PollAsync(device);
        var access = await host.AccessAsync(issued);
        Assert.Equal(host.Epoch.ToString(), access.GetProperty("epoch").GetString());
        var scopes = access.GetProperty("scopes").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(write, scopes.Contains(McpPermissions.TrackingWrite));
        await host.AssertConsentOwnerAsync(access, write);
        await host.RestartAsync();
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(await host.RefreshAsync(issued)));
        Assert.NotEqual(HttpStatusCode.NoContent, await host.DecideAsync(device, browser));
        await host.WithDbAsync(async db => Assert.Single(await db.McpGrants.ToListAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consent_DenialAndReplay_LeaveNoConnection(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, realConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        var device = await host.DeviceAsync();
        host.ApproveConsent = false;
        Assert.Equal(HttpStatusCode.NoContent, await host.DecideAsync(device, browser));
        Assert.Equal(Errors.AccessDenied, (await host.PollAsync(device)).GetProperty("error").GetString());
        host.ApproveConsent = true;
        Assert.NotEqual(HttpStatusCode.NoContent, await host.DecideAsync(device, browser));
        await host.WithDbAsync(async db =>
        {
            Assert.Empty(await db.McpGrants.ToListAsync());
            Assert.Empty(await db.Set<McpProtocolAuthorization>().ToListAsync());
            Assert.All(await db.Set<McpProtocolToken>().ToListAsync(), row => Assert.Equal(Statuses.Rejected, row.Status));
        });
    }

    public static IEnumerable<object[]> ConsentIdentityCases() =>
        from postgres in new[] { false, true }
        from defect in new[] { "missing", "corrupt", "password", "link", "deletion-token", "foreign-tenant",
            "foreign-family", "epoch", "browser-logout", "provider", "deletion", "unrequested-write" }
        select new object[] { postgres, defect };

    [Theory]
    [MemberData(nameof(ConsentIdentityCases))]
    public async Task Consent_UntrustedOrStaleBrowser_CannotApproveOrDeny(bool postgres, string defect)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, realConsent: true);
        var browser = await host.SeedConsentBrowserAsync(defect);
        var device = await host.DeviceAsync();
        if (defect is "provider" or "epoch" or "deletion") await host.RevokeAsync(defect);
        if (defect == "browser-logout") await host.LocalLogoutAsync();
        host.ConsentTrackingWrite = defect == "unrequested-write";
        Assert.NotEqual(HttpStatusCode.NoContent, await host.DecideAsync(device, browser));
        if (defect != "unrequested-write")
        {
            host.ApproveConsent = false;
            Assert.NotEqual(HttpStatusCode.NoContent, await host.DecideAsync(device, browser));
        }
        await host.WithDbAsync(async db =>
        {
            Assert.Empty(await db.McpGrants.ToListAsync());
            Assert.Empty(await db.Set<McpProtocolAuthorization>().ToListAsync());
            Assert.Equal(Statuses.Inactive, (await db.Set<McpProtocolToken>()
                .SingleAsync(row => row.Type == TokenTypeIdentifiers.Private.DeviceCode)).Status);
            Assert.Equal(Statuses.Valid, (await db.Set<McpProtocolToken>()
                .SingleAsync(row => row.Type == TokenTypeIdentifiers.Private.UserCode)).Status);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Consent_ConcurrentDecisions_CreateAtMostOneFamily(bool postgres, bool secondOwner)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, realConsent: true);
        var first = await host.SeedConsentBrowserAsync();
        var device = await host.DeviceAsync();
        if (secondOwner) host.SwitchConsentOwner();
        var second = secondOwner ? await host.SeedConsentBrowserAsync() : first;
        var results = await Task.WhenAll(host.DecideAsync(device, first), host.DecideAsync(device, second));
        Assert.Single(results, status => status == HttpStatusCode.NoContent);
        await host.WithDbAsync(async db =>
        {
            var grant = Assert.Single(await db.McpGrants.ToListAsync());
            Assert.Single(await db.Set<McpProtocolAuthorization>().ToListAsync());
            Assert.Equal(secondOwner ? 3 : 2, await db.AccountSessions.CountAsync());
            var deviceEntry = await db.Set<McpProtocolToken>().Include(row => row.Authorization)
                .SingleAsync(row => row.Type == TokenTypeIdentifiers.Private.DeviceCode);
            Assert.Equal(grant.ProtocolAuthorizationId, deviceEntry.Authorization!.Id);
            Assert.Equal(grant.UserAccountId.ToString(), deviceEntry.Subject);
        });
        Assert.True((await host.PollAsync(device)).TryGetProperty("access_token", out _));
    }

    [Theory]
    [InlineData(false, "commit")]
    [InlineData(true, "commit")]
    [InlineData(false, "late-reject")]
    [InlineData(true, "late-reject")]
    [InlineData(false, "expired")]
    [InlineData(true, "expired")]
    [InlineData(false, "expired-during")]
    [InlineData(true, "expired-during")]
    public async Task Consent_Failure_RollsBackGrantBindingAndConsumption(bool postgres, string failure)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, realConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        var device = await host.DeviceAsync();
        host.FailTokenCommit = failure == "commit";
        host.RejectConsentAfterBinding = failure == "late-reject";
        if (failure == "expired") host.Clock.Advance = TimeSpan.FromMinutes(11);
        if (failure == "expired-during") host.AfterConsentAdmission = () =>
        {
            host.Clock.Advance = TimeSpan.FromMinutes(11);
            return Task.CompletedTask;
        };
        Assert.NotEqual(HttpStatusCode.NoContent, await host.DecideAsync(device, browser));
        await host.WithDbAsync(async db =>
        {
            Assert.Empty(await db.McpGrants.ToListAsync());
            Assert.Empty(await db.Set<McpProtocolAuthorization>().ToListAsync());
            Assert.Single(await db.AccountSessions.ToListAsync());
            Assert.Equal(Statuses.Inactive, (await db.Set<McpProtocolToken>()
                .SingleAsync(row => row.Type == TokenTypeIdentifiers.Private.DeviceCode)).Status);
            Assert.All(await db.Set<McpProtocolToken>().Include(row => row.Authorization).ToListAsync(),
                row => Assert.Null(row.Authorization));
        });
        host.FailTokenCommit = false;
        host.RejectConsentAfterBinding = false;
        host.Clock.Advance = TimeSpan.Zero;
        host.AfterConsentAdmission = null;
        Assert.Equal(HttpStatusCode.NoContent, await host.DecideAsync(device, browser));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consent_AlreadyBoundDevice_CannotSelectOrRevokeAnExistingGrant(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, realConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        var first = await host.DeviceAsync();
        Assert.Equal(HttpStatusCode.NoContent, await host.DecideAsync(first, browser));
        var issued = await host.PollAsync(first);
        var second = await host.DeviceAsync();
        await host.WithDbAsync(async db =>
        {
            var entry = await db.Set<McpProtocolToken>().SingleAsync(row => row.Type == TokenTypeIdentifiers.Private.DeviceCode
                && row.Status == Statuses.Inactive);
            entry.Authorization = await db.Set<McpProtocolAuthorization>().SingleAsync();
            await db.SaveChangesAsync();
        });
        Assert.NotEqual(HttpStatusCode.NoContent, await host.DecideAsync(second, browser));
        Assert.NotEqual(HttpStatusCode.NoContent, await host.DecideAsync(second, browser, approve: false));
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(issued));
        await host.WithDbAsync(async db => Assert.Single(await db.McpGrants.ToListAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consent_ApprovalRacesDenial_OnlyOneDecisionCommits(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, realConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        var device = await host.DeviceAsync();
        var results = await Task.WhenAll(host.DecideAsync(device, browser, approve: true),
            host.DecideAsync(device, browser, approve: false));
        Assert.Single(results, status => status == HttpStatusCode.NoContent);
        await host.WithDbAsync(async db =>
        {
            var count = await db.McpGrants.CountAsync();
            Assert.Equal(results[0] == HttpStatusCode.NoContent ? 1 : 0, count);
            Assert.Equal(1 + count, await db.AccountSessions.CountAsync());
        });
        var poll = await host.PollAsync(device);
        Assert.Equal(results[0] == HttpStatusCode.NoContent, poll.TryGetProperty("access_token", out _));
        if (results[1] == HttpStatusCode.NoContent)
            Assert.Equal(Errors.AccessDenied, poll.GetProperty("error").GetString());
    }

    public static IEnumerable<object[]> ConsentRaceCases() =>
        from postgres in new[] { false, true }
        from approvalWins in new[] { false, true }
        from change in new[] { "provider", "epoch", "deletion", "browser" }
        select new object[] { postgres, approvalWins, change };

    [Theory]
    [MemberData(nameof(ConsentRaceCases))]
    public async Task Consent_ConcurrentLifecycleChange_OrdersDecisionAndRevocation(
        bool postgres, bool approvalWins, string change)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, realConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        var device = await host.DeviceAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task> pause = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
        };
        if (approvalWins) host.AfterConsentAdmission = pause;
        else host.BeforeConsentRequest = pause;
        var decision = Task.Run(() => host.DecideAsync(device, browser));
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
                Assert.Equal(HttpStatusCode.NoContent, await decision.WaitAsync(TimeSpan.FromSeconds(30)));
                await revoke.WaitAsync(TimeSpan.FromSeconds(30));
            }
            else
            {
                if (change == "browser") await host.LocalLogoutAsync(); else await host.RevokeAsync(change);
                release.TrySetResult();
                Assert.NotEqual(HttpStatusCode.NoContent, await decision.WaitAsync(TimeSpan.FromSeconds(30)));
            }
        }
        finally { release.TrySetResult(); }
        host.AfterConsentAdmission = null;
        host.BeforeConsentRequest = null;
        var issued = await host.PollAsync(device);
        Assert.Equal(approvalWins && change == "browser", issued.TryGetProperty("access_token", out _));
        await host.WithDbAsync(async db =>
        {
            if (!approvalWins) Assert.Empty(await db.McpGrants.ToListAsync());
            else
            {
                var grant = Assert.Single(await db.McpGrants.ToListAsync());
                Assert.Equal(change == "browser", grant.RevokedAtUtc is null);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consent_ProviderLogoutAfterLocalLogout_RevokesDerivedIndependentConnections(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, realConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        var first = await host.DeviceAsync();
        Assert.Equal(HttpStatusCode.NoContent, await host.DecideAsync(first, browser));
        var tokens = await host.PollAsync(first);
        var second = await host.DeviceAsync();
        Assert.Equal(HttpStatusCode.NoContent, await host.DecideAsync(second, browser));
        var secondTokens = await host.PollAsync(second);
        var foreignTokens = await host.OtherConsentOwnerTokensAsync();
        await host.LocalLogoutAsync();
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(tokens));
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(secondTokens));
        await host.RevokeAsync("provider");
        Assert.Equal(HttpStatusCode.Unauthorized, await host.AccessStatusAsync(tokens));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.AccessStatusAsync(secondTokens));
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(foreignTokens));
        Assert.False((await host.RefreshAsync(tokens)).TryGetProperty("access_token", out _));
        await host.WithDbAsync(async db =>
        {
            Assert.Equal(2, await db.McpGrants.CountAsync(grant => grant.RevokedAtUtc != null));
            Assert.Equal(1, await db.McpGrants.CountAsync(grant => grant.RevokedAtUtc == null));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consent_RequestAbortedAfterBinding_RollsBackEverything(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(postgres: postgres, realConsent: true);
        var browser = await host.SeedConsentBrowserAsync();
        var device = await host.DeviceAsync();
        using var abort = new CancellationTokenSource();
        host.ConsentRequestCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterConsentAdmission = () => { abort.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.DecideAsync(device, browser, abort.Token));
        await host.ConsentRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await host.WithDbAsync(async db =>
        {
            Assert.Empty(await db.McpGrants.ToListAsync());
            Assert.Empty(await db.Set<McpProtocolAuthorization>().ToListAsync());
            Assert.Single(await db.AccountSessions.ToListAsync());
        });
        host.AfterConsentAdmission = null;
        Assert.Equal(HttpStatusCode.NoContent, await host.DecideAsync(device, browser));
    }

    private sealed partial class DeviceHost
    {
        private readonly JwtSettings browserSettings = new(true, "GameHub.Consent.Tests", "GameHub.Browser",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), 60, 30);
        internal bool ApproveConsent { get; set; } = true;
        internal bool ConsentTrackingWrite { get; set; }
        internal bool RejectConsentAfterBinding { get; set; }
        internal Func<Task>? BeforeConsentRequest { get; set; }
        internal Func<Task>? AfterConsentAdmission { get; set; }
        internal TaskCompletionSource? ConsentRequestCompleted { get; set; }

        private void ConfigureBrowserConsent(WebApplicationBuilder builder)
        {
            builder.Services.AddScoped<McpDeviceConsent>();
            builder.Services.AddAuthentication().AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new()
                {
                    ValidateIssuerSigningKey = true, IssuerSigningKey = browserSettings.CreateSecurityKey(),
                    ValidateIssuer = true, ValidIssuer = browserSettings.Issuer,
                    ValidateAudience = true, ValidAudience = browserSettings.Audience,
                    ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
                };
            });
            builder.Services.AddOpenIddict().AddServer(options => options
                .AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler => handler
                    .UseInlineHandler(async context =>
                    {
                        if (context.EndpointType != OpenIddictServerEndpointType.EndUserVerification) return;
                        if (AfterConsentAdmission is not null) await AfterConsentAdmission();
                        if (RejectConsentAfterBinding) context.Reject(Errors.AccessDenied);
                    }).SetOrder(OpenIddictServerHandlers.RedeemTokenEntry.Descriptor.Order - 400)));
        }

        internal async Task<string?> SeedConsentBrowserAsync(string? defect = null)
        {
            UserAccount? owner = null;
            await WithDbAsync(async db =>
            {
                owner = await db.UserAccounts.FindAsync(account);
                if (owner is not null) return;
                var now = DateTime.UtcNow.AddSeconds(-1);
                if (!await db.Tenants.AnyAsync(row => row.Id == tenant))
                    db.Tenants.Add(new() { Id = tenant, Name = "Consent fixture", CreatedAtUtc = now });
                owner = new() { Id = account, TenantId = tenant, SessionVersion = Epoch,
                    Email = account + "@example.test", NormalizedEmail = account + "@EXAMPLE.TEST",
                    PasswordHash = "synthetic", CreatedAtUtc = now };
                db.UserAccounts.Add(owner);
                db.AccountSessions.Add(new() { Id = browser, TenantId = tenant, UserAccountId = account, CreatedAtUtc = now });
                var identity = Guid.NewGuid();
                db.ExternalIdentities.Add(new() { Id = identity, TenantId = tenant, UserAccountId = account,
                    Issuer = "https://provider.example", Subject = account.ToString(), CreatedAtUtc = now });
                if (defect != "password") db.OidcProviderSessions.Add(new() { Id = provider,
                    TenantId = tenant, UserAccountId = account, ExternalIdentityId = identity, AccountSessionId = browser,
                    SessionKeyHash = OidcProviderSessionKey.Hash("https://provider.example", provider.ToString()), CreatedAtUtc = now });
                await db.SaveChangesAsync();
            });
            var service = new JwtTokenService(browserSettings, Clock);
            if (defect == "missing") return null;
            if (defect == "foreign-tenant") owner!.TenantId = Guid.NewGuid();
            var issued = defect switch
            {
                "link" => service.CreateOidcLinkToken(owner!),
                "deletion-token" => service.CreateAccountDeletionToken(owner!),
                _ => service.CreateToken(owner!, defect == "foreign-family" ? Guid.NewGuid() : browser)
            };
            return defect == "corrupt" ? issued.AccessToken + "corruption" : issued.AccessToken;
        }

        internal async Task<HttpStatusCode> DecideAsync(JsonElement device, string? browserToken,
            CancellationToken cancellation = default, bool? approve = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, McpDeviceProtocol.VerifyPath)
            {
                Content = new FormUrlEncodedContent([new("user_code", device.GetProperty("user_code").GetString()!)])
            };
            if (browserToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", browserToken);
            request.Headers.Add("X-Test-Consent-Decision", (approve ?? ApproveConsent) ? "approve" : "deny");
            using var response = await Client.SendAsync(request, cancellation);
            var body = await response.Content.ReadAsStringAsync(cancellation);
            Assert.DoesNotContain("access_token", body);
            Assert.DoesNotContain("refresh_token", body);
            if (response.StatusCode == HttpStatusCode.NoContent) Assert.Empty(body);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var logs = string.Join("", Logs);
            Assert.DoesNotContain(device.GetProperty("user_code").GetString()!, logs);
            Assert.DoesNotContain(device.GetProperty("device_code").GetString()!, logs);
            if (browserToken is not null) Assert.DoesNotContain(browserToken, logs);
            return response.StatusCode;
        }

        internal Task AssertConsentOwnerAsync(JsonElement access, bool write) => WithDbAsync(async db =>
        {
            var grant = await db.McpGrants.SingleAsync();
            Assert.Equal(account, grant.UserAccountId);
            Assert.Equal(tenant, grant.TenantId);
            Assert.Equal(provider, grant.OidcProviderSessionId);
            Assert.NotEqual(browser, grant.AccountSessionId);
            Assert.Equal(grant.AccountSessionId.ToString(), access.GetProperty("family").GetString());
            Assert.Equal(write ? McpPermissions.ReadAndWrite : McpPermissions.LibraryRead, grant.Scopes);
            var entry = await db.Set<McpProtocolToken>().Include(row => row.Authorization)
                .SingleAsync(row => row.Type == TokenTypeIdentifiers.Private.DeviceCode);
            Assert.Equal(grant.ProtocolAuthorizationId, entry.Authorization!.Id);
            Assert.Equal(grant.UserAccountId, entry.Authorization.UserAccountId);
        });

        internal async Task<JsonElement> OtherConsentOwnerTokensAsync()
        {
            var original = (account, browser, provider, tenant);
            SwitchConsentOwner();
            try
            {
                var ticket = await SeedConsentBrowserAsync();
                var device = await DeviceAsync();
                Assert.Equal(HttpStatusCode.NoContent, await DecideAsync(device, ticket));
                return await PollAsync(device);
            }
            finally { (account, browser, provider, tenant) = original; }
        }
    }
}
