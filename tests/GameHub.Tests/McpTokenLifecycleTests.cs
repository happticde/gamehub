using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Tests;

public sealed partial class McpDeviceProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Token_RealRotation_PreservesOwnerEpochAndAbsoluteExpiry(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        host.GrantLifetime = TimeSpan.FromMinutes(2);
        host.Epoch = (long)int.MaxValue + 17;
        var device = await host.DeviceAsync();
        await host.ApproveAsync(device);
        var issued = await host.PollAsync(device);
        Assert.InRange(issued.GetProperty("expires_in").GetInt32(), 100, 120);
        var original = await host.AccessAsync(issued);
        Assert.Equal(host.Epoch.ToString(), original.GetProperty("epoch").GetString());
        Assert.Equal(DeviceHost.Resource, original.GetProperty("audiences")[0].GetString());
        Assert.Equal(new[] { "library:read", "offline_access" }, original.GetProperty("scopes")
            .EnumerateArray().Select(scope => scope.GetString()).ToArray());
        await host.RestartAsync();
        for (var i = 0; i < 2; i++)
        {
            issued = await host.RefreshAsync(issued);
            var rotated = await host.AccessAsync(issued);
            foreach (var field in new[] { "account", "tenant", "family", "epoch" })
                Assert.Equal(original.GetProperty(field).GetString(), rotated.GetProperty(field).GetString());
        }
        await host.WithDbAsync(async db =>
        {
            var grant = await db.McpGrants.SingleAsync();
            Assert.Equal(host.Epoch, grant.ApprovedSessionVersion);
            var tokens = await db.Set<McpProtocolToken>().Where(row => row.Type == TokenTypeIdentifiers.AccessToken
                || row.Type == TokenTypeIdentifiers.RefreshToken).ToListAsync();
            Assert.Equal(6, tokens.Count);
            Assert.All(tokens, token => Assert.True(token.ExpirationDate <= grant.ExpiresAtUtc));
            Assert.All(tokens.Where(token => token.Type == TokenTypeIdentifiers.RefreshToken), token =>
                Assert.InRange((grant.ExpiresAtUtc - token.ExpirationDate!.Value).TotalSeconds, 0, 2));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_ConfirmedReuse_EndsOnlyItsConnectionAndRejectsSuccessors(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var first = await host.ApprovedTokensAsync();
        var successor = await host.RefreshAsync(first);
        var sameOwner = await host.ApprovedTokensAsync();
        host.SwitchConsentOwner();
        var otherTenant = await host.ApprovedTokensAsync();
        var request = DeviceHost.TokenFields(first.GetProperty("refresh_token").GetString()!, refresh: true);
        var wrongResource = request.Select(field => field.Key == Parameters.Resource
            ? new KeyValuePair<string, string>(field.Key, "https://issuer.example/other") : field).ToList();
        Assert.Equal(Errors.InvalidTarget, (await host.PostAsync(McpDeviceProtocol.TokenPath, wrongResource))
            .GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(successor));
        var corrupted = first.GetProperty("refresh_token").GetString()!;
        corrupted = (corrupted[0] == 'A' ? "B" : "A") + corrupted[1..];
        Assert.Equal(Errors.InvalidGrant, (await host.PostAsync(McpDeviceProtocol.TokenPath,
            DeviceHost.TokenFields(corrupted, refresh: true))).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(successor));
        Assert.Equal(Errors.InvalidGrant, (await host.RefreshAsync(first)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, await host.AccessStatusAsync(first));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.AccessStatusAsync(successor));
        Assert.Equal(Errors.InvalidGrant, (await host.RefreshAsync(successor)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(sameOwner));
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(otherTenant));
        Assert.True((await host.RefreshAsync(sameOwner)).TryGetProperty("access_token", out _));
        Assert.True((await host.RefreshAsync(otherTenant)).TryGetProperty("access_token", out _));
        await host.WithDbAsync(async db =>
        {
            var revoked = await db.McpGrants.SingleAsync(row => row.RevokedAtUtc != null);
            Assert.NotNull((await db.AccountSessions.FindAsync(revoked.AccountSessionId))!.RevokedAtUtc);
            Assert.Equal(Statuses.Revoked, (await db.Set<McpProtocolAuthorization>()
                .SingleAsync(row => row.Id == revoked.ProtocolAuthorizationId)).Status);
            Assert.All(await db.Set<McpProtocolToken>().Where(row => row.Authorization != null
                && row.Authorization.Id == revoked.ProtocolAuthorizationId).ToListAsync(), token =>
                Assert.Equal(Statuses.Revoked, token.Status));
            Assert.Equal(2, await db.McpGrants.CountAsync(row => row.RevokedAtUtc == null));
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Token_GrantExpiresBeforeOrDuringIssuance_NeverExtendsRenewal(bool postgres, bool duringIssuance)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var first = await host.ApprovedTokensAsync();
        if (duringIssuance) host.AfterAdmission = () =>
        {
            host.Clock.Advance = TimeSpan.FromDays(31);
            return Task.CompletedTask;
        };
        else host.Clock.Advance = TimeSpan.FromDays(31);
        Assert.Equal(Errors.InvalidGrant, (await host.RefreshAsync(first)).GetProperty("error").GetString());
        await host.WithDbAsync(async db =>
        {
            Assert.Equal(2, await db.Set<McpProtocolToken>().CountAsync(row => row.Type == TokenTypeIdentifiers.AccessToken
                || row.Type == TokenTypeIdentifiers.RefreshToken));
            Assert.Equal(Statuses.Valid, (await db.Set<McpProtocolToken>()
                .SingleAsync(row => row.Type == TokenTypeIdentifiers.RefreshToken)).Status);
            Assert.Equal(0, (await db.McpGrants.SingleAsync()).ApprovedSessionVersion);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_ConcurrentUseAndReplay_LeavesNoSurvivingSuccessor(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var first = await host.ApprovedTokensAsync();
        var results = await Task.WhenAll(host.RefreshAsync(first), host.RefreshAsync(first));
        var winner = Assert.Single(results, result => result.TryGetProperty("access_token", out _));
        // A serialization failure can precede confirmation. A later replay confirms reuse and
        // closes the connection; no automatic retry is performed by the production middleware.
        Assert.Equal(Errors.InvalidGrant, (await host.RefreshAsync(first)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, await host.AccessStatusAsync(winner));
        Assert.Equal(Errors.InvalidGrant, (await host.RefreshAsync(winner)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_ReplayRevocationCommitFails_NoPartialRevocationSurvives(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var first = await host.ApprovedTokensAsync();
        var successor = await host.RefreshAsync(first);
        host.FailTokenCommit = true;
        Assert.Equal(Errors.TemporarilyUnavailable, (await host.RefreshAsync(first)).GetProperty("error").GetString());
        host.FailTokenCommit = false;
        Assert.Equal(HttpStatusCode.OK, await host.AccessStatusAsync(successor));
        await host.WithDbAsync(async db => Assert.Null((await db.McpGrants.SingleAsync()).RevokedAtUtc));
        Assert.Equal(Errors.InvalidGrant, (await host.RefreshAsync(first)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, await host.AccessStatusAsync(successor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Token_DeviceRedemptionLateFailure_PreservesOneTimeRetry(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var device = await host.DeviceAsync();
        await host.ApproveAsync(device);
        host.RejectAfterGeneration = true;
        Assert.Equal(Errors.InvalidGrant, (await host.PollAsync(device)).GetProperty("error").GetString());
        host.RejectAfterGeneration = false;
        // Rejection must still retain the durable polling interval.
        Assert.Equal(Errors.SlowDown, (await host.PollAsync(device)).GetProperty("error").GetString());
        await host.ResetPollingAsync();
        Assert.True((await host.PollAsync(device)).TryGetProperty("access_token", out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Token_LateRejectionOrCommitFailure_RollsBackConsumptionAndCredentials(bool postgres, bool commitFailure)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var first = await host.ApprovedTokensAsync();
        host.FailTokenCommit = commitFailure;
        host.RejectAfterGeneration = !commitFailure;
        var failed = await host.RefreshAsync(first);
        Assert.Equal(commitFailure ? Errors.TemporarilyUnavailable : Errors.InvalidGrant,
            failed.GetProperty("error").GetString());
        await host.WithDbAsync(async db =>
        {
            Assert.Equal(2, await db.Set<McpProtocolToken>().CountAsync(row => row.Type == TokenTypeIdentifiers.AccessToken
                || row.Type == TokenTypeIdentifiers.RefreshToken));
            Assert.All(await db.Set<McpProtocolToken>().Where(row => row.Type == TokenTypeIdentifiers.RefreshToken)
                .ToListAsync(), token => Assert.Equal(Statuses.Valid, token.Status));
        });
        host.FailTokenCommit = false;
        host.RejectAfterGeneration = false;
        Assert.True((await host.RefreshAsync(first)).TryGetProperty("access_token", out _));
    }

    public static IEnumerable<object[]> TokenRaceCases() =>
        from postgres in new[] { false, true }
        from refresh in new[] { false, true }
        from issuanceWins in new[] { false, true }
        from change in new[] { "provider", "epoch", "deletion", "family", "connection" }
        select new object[] { postgres, refresh, issuanceWins, change };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Token_RequestAbortedAfterAdmission_RollsBackBeforeReturningCredentials(bool postgres)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var first = await host.ApprovedTokensAsync();
        using var abort = new CancellationTokenSource();
        host.TokenRequestCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterAdmission = () => { abort.Cancel(); return Task.CompletedTask; };
        using var request = new HttpRequestMessage(HttpMethod.Post, McpDeviceProtocol.TokenPath)
        {
            Content = new FormUrlEncodedContent(DeviceHost.TokenFields(first.GetProperty("refresh_token").GetString()!, refresh: true))
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Client.SendAsync(request, abort.Token));
        await host.TokenRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await host.WithDbAsync(async db =>
        {
            Assert.Equal(2, await db.Set<McpProtocolToken>().CountAsync(row => row.Type == TokenTypeIdentifiers.AccessToken
                || row.Type == TokenTypeIdentifiers.RefreshToken));
            Assert.Equal(Statuses.Valid, (await db.Set<McpProtocolToken>()
                .SingleAsync(row => row.Type == TokenTypeIdentifiers.RefreshToken)).Status);
        });
        host.AfterAdmission = null;
        Assert.True((await host.RefreshAsync(first)).TryGetProperty("access_token", out _));
    }

    [Theory]
    [MemberData(nameof(TokenRaceCases))]
    public async Task Token_ConcurrentLifecycleChange_OrdersIssuanceAndRevocation(
        bool postgres, bool refresh, bool issuanceWins, string change)
    {
        if (!DeviceHost.CanRun(postgres)) return;
        await using var host = await DeviceHost.CreateAsync(fixture: true, postgres: postgres);
        var device = await host.DeviceAsync();
        await host.ApproveAsync(device);
        var initial = refresh ? await host.PollAsync(device) : default;
        // Local browser logout must preserve the provider provenance for independent connections.
        if (change == "provider") await host.LocalLogoutAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task> pause = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
        };
        if (issuanceWins) host.AfterAdmission = pause;
        // SQLite's serializable transaction already reserves its writer at request admission.
        // Pause before that transaction to exercise a genuinely winning lifecycle commit.
        else host.BeforeTokenRequest = pause;
        var issue = Task.Run(() => refresh ? host.RefreshAsync(initial) : host.PollAsync(device));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            if (issuanceWins)
            {
                host.LifecycleStarting = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var revoke = Task.Run(() => host.RevokeAsync(change));
                await host.LifecycleStarting.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await Assert.ThrowsAsync<TimeoutException>(() => revoke.WaitAsync(TimeSpan.FromMilliseconds(150)));
                release.TrySetResult();
                Assert.True((await issue.WaitAsync(TimeSpan.FromSeconds(30))).TryGetProperty("access_token", out _));
                await revoke.WaitAsync(TimeSpan.FromSeconds(30));
            }
            else
            {
                await host.RevokeAsync(change);
                release.TrySetResult();
                var denied = await issue.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(denied.TryGetProperty("error", out _));
                Assert.False(denied.TryGetProperty("access_token", out _));
            }
        }
        finally { release.TrySetResult(); }
        host.BeforeTokenRequest = null;
        host.AfterAdmission = null;
        var result = await issue;
        if (result.TryGetProperty("access_token", out _))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await host.AccessStatusAsync(result));
            Assert.Equal(Errors.InvalidGrant, (await host.RefreshAsync(result)).GetProperty("error").GetString());
        }
        await host.WithDbAsync(async db =>
        {
            Assert.NotNull((await db.McpGrants.SingleAsync()).RevokedAtUtc);
            Assert.Empty(await db.Set<McpProtocolToken>().Where(row => row.Status == Statuses.Valid
                && (row.Type == TokenTypeIdentifiers.AccessToken || row.Type == TokenTypeIdentifiers.RefreshToken)).ToListAsync());
        });
    }

    private sealed partial class DeviceHost
    {
        internal TimeSpan GrantLifetime { get; set; } = TimeSpan.FromDays(30);
        internal long Epoch { get; set; }
        internal AdvancingClock Clock { get; } = new();
        internal Func<Task>? BeforeTokenRequest { get; set; }
        internal Func<Task>? AfterAdmission { get; set; }
        internal bool RejectAfterGeneration { get; set; }
        internal bool FailTokenCommit { get; set; }
        internal TaskCompletionSource? LifecycleStarting { get; set; }
        internal TaskCompletionSource? TokenRequestCompleted { get; set; }

        internal void SwitchConsentOwner()
        {
            account = Guid.NewGuid(); browser = Guid.NewGuid(); provider = Guid.NewGuid(); tenant = Guid.NewGuid();
        }

        internal async Task<JsonElement> ApprovedTokensAsync()
        {
            var device = await DeviceAsync();
            await ApproveAsync(device);
            var issued = await PollAsync(device);
            Assert.True(issued.TryGetProperty("access_token", out _));
            return issued;
        }

        internal Task<JsonElement> RefreshAsync(JsonElement issued) => PostAsync(McpDeviceProtocol.TokenPath,
            TokenFields(issued.GetProperty("refresh_token").GetString()!, refresh: true));

        private HttpRequestMessage AccessRequest(JsonElement issued)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/test/access");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issued.GetProperty("access_token").GetString());
            return request;
        }

        internal async Task<JsonElement> AccessAsync(JsonElement issued)
        {
            using var request = AccessRequest(issued);
            using var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        internal async Task<HttpStatusCode> AccessStatusAsync(JsonElement issued)
        {
            using var request = AccessRequest(issued);
            using var response = await Client.SendAsync(request);
            return response.StatusCode;
        }

        internal Task LocalLogoutAsync() => WithDbAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await AccountSessionRevocation.RevokeFamilyAsync(db, browser, account, tenant, DateTime.UtcNow);
            await transaction.CommitAsync();
        });

        internal Task RevokeAsync(string change) => WithDbAsync(async db =>
        {
            if (change == "connection")
            {
                var controller = new GameHub.Core.Controllers.McpConnectionsController(db,
                    new DefaultTenantContext(), TimeProvider.System,
                    NullLogger<GameHub.Core.Controllers.McpConnectionsController>.Instance)
                {
                    ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
                    {
                        HttpContext = new DefaultHttpContext
                        {
                            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
                                new(System.Security.Claims.ClaimTypes.NameIdentifier, account.ToString()),
                                new(GameHubClaimTypes.TenantId, tenant.ToString()),
                                new(GameHubClaimTypes.AccountSessionId, browser.ToString())], "Bearer"))
                        }
                    }
                };
                Assert.IsType<Microsoft.AspNetCore.Mvc.NoContentResult>(await controller.Revoke(
                    await db.McpGrants.Select(row => row.Id).SingleAsync(), default));
                return;
            }
            if (change == "provider")
            {
                var service = new OidcBackChannelLogoutService(db, new TenantExecutionContext(), TimeProvider.System,
                    NullLogger<OidcBackChannelLogoutService>.Instance);
                await service.ProcessAsync(new("https://provider.example", null, Guid.NewGuid().ToString(),
                    DateTimeOffset.UtcNow, provider.ToString()));
                return;
            }
            await using var transaction = await db.Database.BeginTransactionAsync();
            if (change == "family")
            {
                var grant = await db.McpGrants.SingleAsync();
                await AccountSessionRevocation.RevokeFamilyAsync(db, grant.AccountSessionId, account, tenant, DateTime.UtcNow);
            }
            else
            {
                if (change == "deletion") await db.Tenants.Where(row => row.Id == tenant)
                    .ExecuteUpdateAsync(update => update.SetProperty(row => row.DeletionRequestedAtUtc, DateTime.UtcNow));
                await db.UserAccounts.Where(row => row.Id == account)
                    .ExecuteUpdateAsync(update => update.SetProperty(row => row.SessionVersion, row => row.SessionVersion + 1));
                await AccountSessionRevocation.RevokeAllFamiliesAsync(db, account, tenant, DateTime.UtcNow);
            }
            await transaction.CommitAsync();
        });

        private sealed class TokenCommitInterceptor(DeviceHost host) : DbTransactionInterceptor
        {
            public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
                TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
                CancellationToken cancellationToken = default)
            {
                host.LifecycleStarting?.TrySetResult();
                return ValueTask.FromResult(result);
            }

            public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
                TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            {
                if (host.FailTokenCommit) throw new InvalidOperationException("Synthetic protocol commit failure.");
                return ValueTask.FromResult(result);
            }
        }
    }

    private sealed class AdvancingClock : TimeProvider
    {
        internal TimeSpan Advance { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.Add(Advance);
    }
}
