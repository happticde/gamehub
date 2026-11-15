using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using GameHub.Core.Data;
using GameHub.Core.Services;
using GameHub.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OpenIddict.Abstractions;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace GameHub.Tests;

/// <summary>Real owner/provenance checks and concurrent lifecycle ordering on both providers.</summary>
public sealed class McpGrantAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Creation_UntrustedOrRestrictedIdentity_NeverSelectsAnOwner(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        foreach (var defect in new[] { "anonymous", "operator", "integration", "link-only", "deletion-only",
            "duplicate-account", "duplicate-tenant", "duplicate-epoch", "duplicate-family", "foreign-tenant",
            "foreign-family", "subject-mismatch", "stale-epoch", "negative-epoch", "second-identity" })
        {
            await using var db = fixture.Context();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var identity = (ClaimsIdentity)fixture.Browser().Principal!.Identity!;
            var scheme = JwtBearerDefaults.AuthenticationScheme;
            switch (defect)
            {
                case "anonymous": identity = new ClaimsIdentity(identity.Claims); break;
                case "operator": scheme = ApiKeyAuthenticationHandler.SchemeName; break;
                case "integration": scheme = McpAuthorization.IntegrationScheme; break;
                case "link-only": identity.AddClaim(new(GameHubClaimTypes.SessionPurpose, GameHubClaimTypes.OidcLinkOnlySessionPurpose)); break;
                case "deletion-only": identity.AddClaim(new(GameHubClaimTypes.SessionPurpose, GameHubClaimTypes.AccountDeletionOnlySessionPurpose)); break;
                case "duplicate-account": identity.AddClaim(identity.FindFirst(ClaimTypes.NameIdentifier)!); break;
                case "duplicate-tenant": identity.AddClaim(identity.FindFirst(GameHubClaimTypes.TenantId)!); break;
                case "duplicate-epoch": identity.AddClaim(identity.FindFirst(GameHubClaimTypes.SessionVersion)!); break;
                case "duplicate-family": identity.AddClaim(identity.FindFirst(GameHubClaimTypes.AccountSessionId)!); break;
                case "foreign-tenant": Replace(identity, GameHubClaimTypes.TenantId, fixture.ForeignTenant.ToString()); break;
                case "foreign-family": Replace(identity, GameHubClaimTypes.AccountSessionId, fixture.ForeignBrowser.ToString()); break;
                case "subject-mismatch": Replace(identity, Claims.Subject, fixture.ForeignAccount.ToString()); break;
                case "stale-epoch": Replace(identity, GameHubClaimTypes.SessionVersion, "0"); break;
                case "negative-epoch": Replace(identity, GameHubClaimTypes.SessionVersion, "-1"); break;
            }
            var principal = new ClaimsPrincipal(identity);
            if (defect == "second-identity") principal.AddIdentity(new ClaimsIdentity("another-handler"));
            var ticket = AuthenticateResult.Success(new(principal, scheme));
            Assert.Null(await fixture.Service(db).CreateAsync(ticket, [McpPermissions.LibraryRead], false));
            Assert.Empty(await db.McpGrants.ToListAsync());
            Assert.Empty(await db.Set<McpProtocolAuthorization>().ToListAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Creation_LinkWithoutLiveProviderAndInvalidScopeRequest_FailClosed(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        foreach (var defect in new[] { "password-family", "local-logout", "provider-logout", "epoch", "deletion",
            "no-read", "unknown-scope", "duplicate-scope", "unrequested-write", "no-client" })
        {
            await using var db = fixture.Context();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var ticket = fixture.Browser();
            IReadOnlyCollection<string> scopes = [McpPermissions.LibraryRead];
            switch (defect)
            {
                case "password-family":
                    var passwordFamily = Guid.NewGuid();
                    db.AccountSessions.Add(new() { Id = passwordFamily, UserAccountId = fixture.Account,
                        TenantId = fixture.Tenant, CreatedAtUtc = fixture.Clock.Now });
                    await db.SaveChangesAsync();
                    Replace((ClaimsIdentity)ticket.Principal!.Identity!, GameHubClaimTypes.AccountSessionId, passwordFamily.ToString());
                    break;
                case "local-logout": await fixture.MutateAsync(db, "browser"); break;
                case "provider-logout": await fixture.MutateAsync(db, "provider"); break;
                case "epoch": await fixture.MutateAsync(db, "epoch"); break;
                case "deletion": await fixture.MutateAsync(db, "deletion"); break;
                case "no-read": scopes = [McpPermissions.TrackingWrite]; break;
                case "unknown-scope": scopes = [McpPermissions.LibraryRead, "admin"]; break;
                case "duplicate-scope": scopes = [McpPermissions.LibraryRead, McpPermissions.LibraryRead]; break;
                case "no-client": await db.Set<McpProtocolApplication>().ExecuteDeleteAsync(); break;
            }
            Assert.Null(await fixture.Service(db).CreateAsync(ticket, scopes, defect == "unrequested-write"));
            Assert.Empty(await db.McpGrants.ToListAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Creation_ExplicitConsent_IsIndependentImmutableAndRollsBackAtomically(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        await using var db = fixture.Context();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var read = await fixture.Service(db).CreateAsync(fixture.Browser(),
                [McpPermissions.LibraryRead, McpPermissions.TrackingWrite, Scopes.OfflineAccess], false);
            var write = await fixture.Service(db).CreateAsync(fixture.Browser(),
                [McpPermissions.LibraryRead, McpPermissions.TrackingWrite], true);
            Assert.NotNull(read); Assert.NotNull(write);
            Assert.NotEqual(fixture.BrowserId, read.FamilyId);
            Assert.NotEqual(read.FamilyId, write.FamilyId);
            Assert.NotEqual(read.AuthorizationId, write.AuthorizationId);
            Assert.Equal(fixture.ProviderId, read.ProviderSessionId);
            Assert.Equal(Fixture.Epoch, read.SessionVersion);
            Assert.Equal(McpPermissions.LibraryRead, read.Scopes);
            Assert.Equal(McpPermissions.ReadAndWrite, write.Scopes);
            Assert.Equal(Fixture.Resource.Resource.AbsoluteUri, read.Resource);
            Assert.Equal(fixture.Clock.Now.AddDays(30), read.ExpiresAtUtc);
            var authorization = await db.Set<McpProtocolAuthorization>().SingleAsync(row => row.Id == read.AuthorizationId);
            Assert.Equal("[\"library:read\",\"offline_access\"]", authorization.Scopes);
            Assert.Equal(fixture.Account.ToString(), authorization.Subject);
            Assert.Equal(fixture.Account, authorization.UserAccountId);
            Assert.Equal(fixture.Tenant, authorization.TenantId);
            // A later failed code verification must roll back all three owned rows together.
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Empty(await db.McpGrants.ToListAsync());
        Assert.Empty(await db.Set<McpProtocolAuthorization>().ToListAsync());
        Assert.Equal(2, await db.AccountSessions.CountAsync());
        Assert.Equal(fixture.BrowserId, (await db.OidcProviderSessions.SingleAsync(row => row.Id == fixture.ProviderId)).AccountSessionId);
        Assert.Empty(await db.RefreshTokenSessions.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_ProtocolOrLifecycleMismatch_IsRejectedWithoutChangingConsent(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var grant = await fixture.GrantAsync();
        foreach (var defect in new[] { "provider", "epoch", "deletion", "family", "grant", "authorization",
            "token", "token-expiry", "grant-expiry", "future-grant", "foreign-tenant", "foreign-family",
            "foreign-authorization", "foreign-token", "resource", "presenter", "scope", "permission", "token-type" })
        {
            await using var db = fixture.Context();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var principal = fixture.Integration(grant);
            var identity = (ClaimsIdentity)principal.Identity!;
            switch (defect)
            {
                case "provider" or "epoch" or "deletion" or "family": await fixture.MutateAsync(db, defect, grant); break;
                case "grant": await db.McpGrants.ExecuteUpdateAsync(u => u.SetProperty(row => row.RevokedAtUtc, fixture.Clock.Now)); break;
                case "authorization": await db.Set<McpProtocolAuthorization>().ExecuteUpdateAsync(u => u.SetProperty(row => row.Status, Statuses.Revoked)); break;
                case "token": await db.Set<McpProtocolToken>().ExecuteUpdateAsync(u => u.SetProperty(row => row.Status, Statuses.Redeemed)); break;
                case "token-expiry": await db.Set<McpProtocolToken>().ExecuteUpdateAsync(u => u.SetProperty(row => row.ExpirationDate, fixture.Clock.Now)); break;
                case "grant-expiry": fixture.Clock.Now = fixture.Clock.Now.AddDays(31); break;
                case "future-grant": fixture.Clock.Now = fixture.Clock.Now.AddMinutes(-1); break;
                case "foreign-tenant": Replace(identity, GameHubClaimTypes.TenantId, fixture.ForeignTenant.ToString()); break;
                case "foreign-family": Replace(identity, GameHubClaimTypes.AccountSessionId, fixture.ForeignBrowser.ToString()); break;
                case "foreign-authorization": principal.SetAuthorizationId(Guid.NewGuid().ToString()); break;
                case "foreign-token": principal.SetTokenId(Guid.NewGuid().ToString()); break;
                case "resource": principal.SetResources("https://issuer.example/mcp"); break;
                case "presenter": principal.SetPresenters("another-client"); break;
                case "scope": principal.SetScopes(McpPermissions.LibraryRead, "admin"); break;
            }
            Assert.Null(await fixture.Service(db).AdmitAsync(principal,
                defect == "token-type" ? TokenTypeIdentifiers.RefreshToken : TokenTypeIdentifiers.AccessToken,
                defect == "permission" ? McpPermissions.TrackingWrite : McpPermissions.LibraryRead));
            fixture.Clock.Now = fixture.InitialTime;
        }
        await using var check = fixture.Context();
        var persisted = await check.McpGrants.SingleAsync();
        Assert.Equal(grant.ExpiresAtUtc, persisted.ExpiresAtUtc);
        Assert.Equal(Fixture.Epoch, persisted.ApprovedSessionVersion);
        Assert.Null(persisted.RevokedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_LocalBrowserLogoutPreservesGrant_ActualProviderLogoutStillRevokesIt(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var grant = await fixture.GrantAsync();
        await using var db = fixture.Context();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await fixture.MutateAsync(db, "browser");
            Assert.Null(await fixture.Service(db).CreateAsync(fixture.Browser(), [McpPermissions.LibraryRead], false));
            Assert.NotNull(await fixture.Service(db).AdmitAsync(fixture.Integration(grant), TokenTypeIdentifiers.AccessToken));
            await transaction.CommitAsync();
        }
        await fixture.LogoutAsync(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            Assert.Null(await fixture.Service(db).AdmitAsync(fixture.Integration(grant), TokenTypeIdentifiers.AccessToken));
            Assert.Equal(Statuses.Revoked, (await db.Set<McpProtocolAuthorization>().AsNoTracking().SingleAsync()).Status);
        }
        Assert.Null((await db.AccountSessions.AsNoTracking().SingleAsync(row => row.Id == fixture.ForeignBrowser)).RevokedAtUtc);
    }

    [Theory]
    [InlineData(false, TokenTypeIdentifiers.Private.DeviceCode)]
    [InlineData(true, TokenTypeIdentifiers.Private.DeviceCode)]
    [InlineData(false, TokenTypeIdentifiers.RefreshToken)]
    [InlineData(true, TokenTypeIdentifiers.RefreshToken)]
    [InlineData(false, TokenTypeIdentifiers.AccessToken)]
    [InlineData(true, TokenTypeIdentifiers.AccessToken)]
    public async Task Admission_ExpectedProtocolTokenType_PreservesOriginalConsent(bool postgres, string tokenType)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var grant = await fixture.GrantAsync(tokenType);
        await using var db = fixture.Context();
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.Equal(grant, await fixture.Service(db).AdmitAsync(fixture.Integration(grant), tokenType));
        Assert.Equal(Fixture.Epoch, (await db.UserAccounts.SingleAsync(row => row.Id == fixture.Account)).SessionVersion);
        Assert.Single(await db.McpGrants.ToListAsync());
    }

    public static IEnumerable<object[]> RaceCases() =>
        from postgres in new[] { false, true }
        from existing in new[] { false, true }
        from admissionWins in new[] { false, true }
        from change in new[] { "provider", "epoch", "deletion", "family" }
        where existing || change != "family"
        select new object[] { postgres, existing, admissionWins, change };

    [Theory]
    [MemberData(nameof(RaceCases))]
    public async Task Admission_ConcurrentLifecycleChange_OrdersCommitAndDeniesAfterRevocation(
        bool postgres, bool existing, bool admissionWins, string change)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        if (fixture is null) return;
        var grant = existing ? await fixture.GrantAsync() : null;
        await using var winnerDb = fixture.Context();
        await using var winner = await winnerDb.Database.BeginTransactionAsync();
        McpGrantAdmissionResult? firstAdmission = null;
        if (admissionWins)
        {
            firstAdmission = await Admit(winnerDb);
            Assert.NotNull(firstAdmission);
            winnerDb.ActivityLogs.Add(new() { TenantId = fixture.Tenant, Timestamp = fixture.Clock.Now,
                EventType = "test", Message = "Admitted operation committed before revocation." });
            await winnerDb.SaveChangesAsync();
        }
        else await fixture.MutateAsync(winnerDb, change, grant);

        var starting = new TransactionStartSignal();
        // SQLite's transaction begin is synchronous internally. A separate worker ensures it
        // can block on the first writer while the test remains able to release that writer.
        var contender = Task.Run(async () =>
        {
            await using var db = fixture.Context(starting);
            if (admissionWins && change == "provider")
            {
                // Exercise the actual back-channel lifecycle path, including its independent
                // transaction, after the admission has acquired the shared provider barrier.
                await fixture.LogoutAsync(db);
                return null;
            }
            await using var transaction = await db.Database.BeginTransactionAsync();
            var admitted = admissionWins ? null : await Admit(db);
            if (admissionWins) await fixture.MutateAsync(db, change, firstAdmission ?? grant);
            if (admitted is not null)
            {
                db.ActivityLogs.Add(new() { TenantId = fixture.Tenant, Timestamp = fixture.Clock.Now,
                    EventType = "test", Message = "Must not survive winning revocation." });
                await db.SaveChangesAsync();
            }
            await transaction.CommitAsync();
            return admitted;
        });
        await starting.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await winner.CommitAsync();
        Assert.Null(await contender.WaitAsync(TimeSpan.FromSeconds(30)));
        await using var check = fixture.Context();
        Assert.Equal(admissionWins ? 1 : 0, await check.ActivityLogs.CountAsync(row => row.EventType == "test"));
        if (admissionWins)
        {
            Assert.NotNull((await check.McpGrants.SingleAsync()).RevokedAtUtc);
            Assert.Equal(Statuses.Revoked, (await check.Set<McpProtocolAuthorization>().SingleAsync()).Status);
        }
        else if (!existing) Assert.Empty(await check.McpGrants.ToListAsync());
        await using var final = await check.Database.BeginTransactionAsync();
        Assert.Null(await Admit(check));
        Assert.Null((await check.AccountSessions.AsNoTracking().SingleAsync(row => row.Id == fixture.ForeignBrowser)).RevokedAtUtc);

        Task<McpGrantAdmissionResult?> Admit(CoreContext db) => grant is null
            ? fixture.Service(db).CreateAsync(fixture.Browser(), [McpPermissions.LibraryRead], false)
            : fixture.Service(db).AdmitAsync(fixture.Integration(grant), TokenTypeIdentifiers.AccessToken);
    }

    [Fact]
    public async Task Admission_WithoutCallerTransaction_ThrowsBeforeCreatingState()
    {
        await using var fixture = (await Fixture.CreateAsync(false))!;
        await using var db = fixture.Context();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service(db)
            .CreateAsync(fixture.Browser(), [McpPermissions.LibraryRead], false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service(db)
            .AdmitAsync(new ClaimsPrincipal(), TokenTypeIdentifiers.AccessToken));
        Assert.Empty(await db.McpGrants.ToListAsync());
    }

    private static void Replace(ClaimsIdentity identity, string type, string value)
    {
        foreach (var claim in identity.FindAll(type).ToArray()) identity.RemoveClaim(claim);
        identity.AddClaim(new(type, value));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTime Now { get; set; } = new(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private sealed class TransactionStartSignal : DbTransactionInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Fixture(string connectionString, bool postgres) : IAsyncDisposable
    {
        public const long Epoch = (long)int.MaxValue + 2;
        public static readonly McpRestResource Resource = new(new("https://issuer.example/"), new("https://issuer.example/api"));
        public Guid Tenant => TenantDefaults.DefaultTenantId;
        public Guid Account { get; } = Guid.NewGuid();
        public Guid BrowserId { get; } = Guid.NewGuid();
        public Guid ProviderId { get; } = Guid.NewGuid();
        public Guid ForeignAccount { get; } = Guid.NewGuid();
        public Guid ForeignTenant { get; } = Guid.NewGuid();
        public Guid ForeignBrowser { get; } = Guid.NewGuid();
        public Clock Clock { get; } = new();
        public DateTime InitialTime { get; private set; }

        public static async Task<Fixture?> CreateAsync(bool postgres)
        {
            string connection;
            if (postgres)
            {
                var configured = Environment.GetEnvironmentVariable("GAMEHUB_TEST_MCP_POSTGRES_CONNECTION");
                if (string.IsNullOrWhiteSpace(configured)) return null;
                connection = new NpgsqlConnectionStringBuilder(configured)
                    { Database = "mcp_admission_" + Guid.NewGuid().ToString("N") }.ConnectionString;
            }
            else connection = $"Data Source={Path.Combine(Path.GetTempPath(), "mcp_admission_" + Guid.NewGuid().ToString("N") + ".db")};Pooling=False;Default Timeout=15";
            var fixture = new Fixture(connection, postgres);
            fixture.InitialTime = fixture.Clock.Now;
            try
            {
                await using var db = fixture.Context();
                await db.Database.MigrateAsync();
                db.Tenants.Add(new() { Id = fixture.ForeignTenant, Name = "Foreign", CreatedAtUtc = fixture.Clock.Now });
                foreach (var (account, tenant, family, name) in new[] {
                    (fixture.Account, fixture.Tenant, fixture.BrowserId, "owner"),
                    (fixture.ForeignAccount, fixture.ForeignTenant, fixture.ForeignBrowser, "foreign") })
                {
                    db.UserAccounts.Add(new() { Id = account, TenantId = tenant, Email = name + "@example.test",
                        NormalizedEmail = name.ToUpperInvariant() + "@EXAMPLE.TEST", PasswordHash = "synthetic",
                        CreatedAtUtc = fixture.Clock.Now, SessionVersion = Epoch });
                    db.AccountSessions.Add(new() { Id = family, UserAccountId = account, TenantId = tenant, CreatedAtUtc = fixture.Clock.Now });
                }
                var external = Guid.NewGuid();
                db.ExternalIdentities.Add(new() { Id = external, UserAccountId = fixture.Account, TenantId = fixture.Tenant,
                    Issuer = "https://provider.example", Subject = "owner", CreatedAtUtc = fixture.Clock.Now });
                db.OidcProviderSessions.Add(new() { Id = fixture.ProviderId, UserAccountId = fixture.Account, TenantId = fixture.Tenant,
                    AccountSessionId = fixture.BrowserId, ExternalIdentityId = external, CreatedAtUtc = fixture.Clock.Now,
                    SessionKeyHash = OidcProviderSessionKey.Hash("https://provider.example", "admission-synthetic-sid") });
                db.Set<McpProtocolApplication>().Add(new() { Id = Guid.NewGuid().ToString(), ClientId = McpDeviceProtocol.ClientId,
                    ClientType = ClientTypes.Public, ConsentType = ConsentTypes.Explicit });
                await db.SaveChangesAsync();
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public CoreContext Context(IInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<CoreContext>()
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
            if (postgres) options.UseNpgsql(connectionString);
            else options.UseSqlite(connectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new(options.Options, new DefaultTenantContext());
        }

        public McpGrantAdmission Service(CoreContext db) => new(db, Resource, Clock);
        public AuthenticateResult Browser()
        {
            var identity = new ClaimsIdentity(JwtBearerDefaults.AuthenticationScheme);
            identity.AddClaims([new(ClaimTypes.NameIdentifier, Account.ToString()), new(Claims.Subject, Account.ToString()),
                new(GameHubClaimTypes.TenantId, Tenant.ToString()), new(GameHubClaimTypes.AccountSessionId, BrowserId.ToString()),
                new(GameHubClaimTypes.SessionVersion, Epoch.ToString(CultureInfo.InvariantCulture))]);
            return AuthenticateResult.Success(new(new ClaimsPrincipal(identity), JwtBearerDefaults.AuthenticationScheme));
        }

        public ClaimsPrincipal Integration(McpGrantAdmissionResult grant)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity("protocol-validated"));
            principal.SetClaim(Claims.Subject, Account.ToString());
            principal.SetClaim(GameHubClaimTypes.TenantId, Tenant.ToString());
            principal.SetClaim(GameHubClaimTypes.AccountSessionId, grant.FamilyId.ToString());
            principal.SetClaim(GameHubClaimTypes.SessionVersion, Epoch.ToString(CultureInfo.InvariantCulture));
            principal.SetAuthorizationId(grant.AuthorizationId).SetTokenId(grant.GrantId.ToString());
            principal.SetResources(Resource.Resource.AbsoluteUri).SetScopes(McpPermissions.LibraryRead)
                .SetPresenters(McpDeviceProtocol.ClientId);
            return principal;
        }

        public async Task<McpGrantAdmissionResult> GrantAsync(string tokenType = TokenTypeIdentifiers.AccessToken)
        {
            await using var db = Context();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var grant = await Service(db).CreateAsync(Browser(), [McpPermissions.LibraryRead], false);
            Assert.NotNull(grant);
            db.Set<McpProtocolToken>().Add(new() { Id = grant.GrantId.ToString(),
                Authorization = await db.Set<McpProtocolAuthorization>().SingleAsync(),
                Application = await db.Set<McpProtocolApplication>().SingleAsync(), Subject = Account.ToString(),
                Type = tokenType, Status = Statuses.Valid,
                CreationDate = Clock.Now, ExpirationDate = Clock.Now.AddMinutes(10) });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return grant;
        }

        public async Task MutateAsync(CoreContext db, string change, McpGrantAdmissionResult? grant = null)
        {
            if (change == "provider")
            {
                await db.OidcProviderSessions.Where(row => row.Id == ProviderId)
                    .ExecuteUpdateAsync(u => u.SetProperty(row => row.ReissueBlockedAtUtc, Clock.Now)
                        .SetProperty(row => row.RevokedAtUtc, Clock.Now));
                await McpGrantLifecycle.RevokeProviderAsync(db, ProviderId, Account, Tenant, Clock.Now);
            }
            else if (change is "epoch" or "deletion")
            {
                if (change == "deletion") await db.Tenants.Where(row => row.Id == Tenant)
                    .ExecuteUpdateAsync(u => u.SetProperty(row => row.DeletionRequestedAtUtc, Clock.Now));
                await db.UserAccounts.Where(row => row.Id == Account)
                    .ExecuteUpdateAsync(u => u.SetProperty(row => row.SessionVersion, row => row.SessionVersion + 1));
                await AccountSessionRevocation.RevokeAllFamiliesAsync(db, Account, Tenant, Clock.Now);
            }
            else await AccountSessionRevocation.RevokeFamilyAsync(db,
                change == "browser" ? BrowserId : grant!.FamilyId, Account, Tenant, Clock.Now);
        }

        public Task<OidcBackChannelLogoutResult> LogoutAsync(CoreContext db) => new OidcBackChannelLogoutService(db,
            new TenantExecutionContext(), Clock, NullLogger<OidcBackChannelLogoutService>.Instance)
            .ProcessAsync(new("https://provider.example", null, Guid.NewGuid().ToString(), Clock.GetUtcNow(), "admission-synthetic-sid"));

        public async ValueTask DisposeAsync()
        {
            await using var db = Context();
            await db.Database.EnsureDeletedAsync();
        }
    }
}
