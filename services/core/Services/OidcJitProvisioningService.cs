using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Atomically creates one personal tenant for a completely validated, previously unknown external
/// identity. Request values and provider claims never select any GameHub identifier or owner.
/// </summary>
public sealed class OidcJitProvisioningService(
    CoreContext db,
    RefreshTokenService refreshTokens,
    TenantExecutionContext tenantExecution,
    OidcSettings oidcSettings,
    MultiUserEnablementSettings multiUser,
    TimeProvider timeProvider,
    ILogger<OidcJitProvisioningService> logger)
{
    /// <summary>
    /// Creates or safely resolves one personal account. Every newly created durable row, including
    /// the refresh session and tenant-owned activity entry, shares one database transaction.
    /// </summary>
    public async Task<OidcJitProvisioningResult> ProvisionAsync(
        ValidatedOidcIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (!oidcSettings.IsEnabled
            || oidcSettings.ProvisioningMode != OidcProvisioningMode.PersonalTenantJit
            || !multiUser.Mode.IsEnabled
            || !string.Equals(identity.Issuer, oidcSettings.Issuer, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(identity.ProviderSessionId))
        {
            return OidcJitProvisioningResult.Denied;
        }

        // Deletion is durable identity lifecycle state. A deleted subject cannot use JIT as an
        // implicit re-registration or subject-replacement path.
        if (await db.DeletedExternalIdentities.AsNoTracking().AnyAsync(
                candidate => candidate.Issuer == identity.Issuer
                    && candidate.Subject == identity.Subject,
                cancellationToken))
        {
            return OidcJitProvisioningResult.Denied;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Personal Library",
            PlatformSyncEnabled = false,
            CreatedAtUtc = nowUtc
        };
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Tenant = tenant,
            AccountType = UserAccountType.ExternalOidc,
            Email = null,
            NormalizedEmail = null,
            DisplayName = "OIDC Account",
            PasswordHash = null,
            CreatedAtUtc = nowUtc,
            LastLoginAtUtc = nowUtc
        };
        var mapping = new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            Issuer = identity.Issuer,
            Subject = identity.Subject,
            UserAccountId = account.Id,
            TenantId = tenant.Id,
            UserAccount = account,
            CreatedAtUtc = nowUtc,
            LastLoginAtUtc = nowUtc
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        using var tenantScope = tenantExecution.BeginScope(tenant.Id);
        db.ExternalIdentities.Add(mapping);
        db.ActivityLogs.Add(new ActivityLog
        {
            Timestamp = nowUtc,
            EventType = "OidcJitProvisioned",
            Message = "Personal tenant created through OIDC JIT provisioning.",
            Details = null
        });
        var refreshToken = refreshTokens.StageOidcIssue(
            account,
            mapping,
            identity.ProviderSessionId!);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("OIDC JIT provisioning completed");
            return OidcJitProvisioningResult.CreateSucceeded(account, refreshToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();

            // The unique issuer/subject mapping is the final concurrency authority. If another
            // first-login transaction won, safely resolve its committed live account and create a
            // normal independent session. Otherwise preserve the generic authentication failure.
            var winner = await db.ExternalIdentities
                .Include(candidate => candidate.UserAccount)
                    .ThenInclude(candidate => candidate!.Tenant)
                .SingleOrDefaultAsync(
                    candidate => candidate.Issuer == identity.Issuer
                        && candidate.Subject == identity.Subject,
                    cancellationToken);
            if (winner?.UserAccount?.Tenant is not null
                && winner.TenantId == winner.UserAccount.TenantId
                && winner.UserAccount.Tenant.DeletionRequestedAtUtc is null)
            {
                var winnerRefreshToken = await refreshTokens.IssueOidcAsync(
                    new OidcAuthenticatedAccount(winner.UserAccount, winner),
                    identity.ProviderSessionId!,
                    cancellationToken);
                if (!winnerRefreshToken.Issued)
                {
                    return OidcJitProvisioningResult.Denied;
                }
                logger.LogInformation("OIDC JIT provisioning resolved a concurrent winner");
                return OidcJitProvisioningResult.CreateSucceeded(
                    winner.UserAccount,
                    winnerRefreshToken.RefreshToken!);
            }

            logger.LogWarning("OIDC JIT provisioning failed without committing partial data");
            return OidcJitProvisioningResult.Denied;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            logger.LogWarning("OIDC JIT provisioning failed without committing partial data");
            return OidcJitProvisioningResult.Denied;
        }
    }
}

/// <summary>Internal result of personal-tenant JIT provisioning.</summary>
public sealed record OidcJitProvisioningResult(
    UserAccount? Account,
    IssuedRefreshToken? RefreshToken)
{
    /// <summary>True only when a live account and refresh session are available.</summary>
    public bool Succeeded => Account is not null && RefreshToken is not null;

    /// <summary>Generic fail-closed result.</summary>
    public static OidcJitProvisioningResult Denied { get; } = new(null, null);

    /// <summary>Creates a successful result from server-owned identity rows.</summary>
    public static OidcJitProvisioningResult CreateSucceeded(
        UserAccount account,
        IssuedRefreshToken refreshToken) => new(account, refreshToken);
}
