using GameHub.Core.Data;
using GameHub.Shared.Authentication;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Validated Core-side prerequisites for explicit production multi-user enablement.
/// Blocker identifiers are deliberately secret-free so an operator status response can expose
/// them without disclosing configured values, account data, or tenant data.
/// </summary>
public sealed record MultiUserEnablementSettings(
    MultiUserMode Mode,
    IReadOnlyList<string> ConfigurationBlockers)
{
    /// <summary>
    /// Allows the seeded default tenant in compatibility mode and every persisted tenant only
    /// after the operator enables production multi-user mode. No request data can alter this rule.
    /// </summary>
    public bool AllowsOidcLogin(Guid tenantId) =>
        tenantId == TenantDefaults.DefaultTenantId || Mode.IsEnabled;

    /// <summary>Loads the immutable switch and validates every Core/BFF trust prerequisite.</summary>
    public static MultiUserEnablementSettings FromConfiguration(
        IConfiguration configuration,
        JwtSettings jwtSettings,
        OidcSettings oidcSettings)
    {
        var mode = MultiUserMode.FromConfiguration(configuration);
        var blockers = new List<string>();

        if (!jwtSettings.IsEnabled)
        {
            blockers.Add("jwt_not_configured");
        }
        if (!oidcSettings.IsEnabled)
        {
            blockers.Add("oidc_not_configured");
        }
        if (!IsStrongHeaderSecret(configuration["Authentication:ApiKey"]))
        {
            blockers.Add("operator_api_key_not_ready");
        }
        if (!IsStrongHeaderSecret(configuration["Services:IngestServiceKey"]))
        {
            blockers.Add("ingest_trust_not_ready");
        }
        if (!ReadRequiredBoolean(configuration, "Authentication:Frontend:Enabled"))
        {
            blockers.Add("frontend_auth_not_enabled");
        }
        if (!ReadRequiredBoolean(configuration, "Authentication:Frontend:SecureCookies"))
        {
            blockers.Add("frontend_secure_cookies_not_enabled");
        }

        var frontendMode = ReadOptionalBoolean(
            configuration,
            "Authentication:Frontend:MultiUserEnabled",
            defaultValue: false);
        if (frontendMode != mode.IsEnabled)
        {
            // This disagreement is unsafe in either direction: the browser-facing BFF must not
            // advertise a mode Core refuses, and Core must not activate a mode the BFF has not.
            throw new InvalidOperationException(
                "Core and frontend multi-user enablement settings must match exactly.");
        }

        var trustSecrets = new[]
        {
            configuration["Authentication:ApiKey"]?.Trim(),
            configuration["Services:IngestServiceKey"]?.Trim(),
            jwtSettings.IsEnabled ? jwtSettings.SigningKey : null,
            oidcSettings.IsEnabled ? oidcSettings.BffServiceKey : null
        }.Where(value => !string.IsNullOrEmpty(value)).Cast<string>().ToArray();
        if (trustSecrets.Distinct(StringComparer.Ordinal).Count() != trustSecrets.Length)
        {
            blockers.Add("trust_secrets_not_independent");
        }

        var settings = new MultiUserEnablementSettings(mode, blockers.Distinct().ToArray());
        if (mode.IsEnabled && settings.ConfigurationBlockers.Count > 0)
        {
            throw new InvalidOperationException(
                $"Guarded multi-user enablement is blocked: {string.Join(", ", settings.ConfigurationBlockers)}.");
        }

        return settings;
    }

    private static bool IsStrongHeaderSecret(string? value)
    {
        var canonical = value?.Trim() ?? "";
        return canonical.Length is >= 32 and <= 512
            && canonical.All(character => character is >= '!' and <= '~');
    }

    private static bool ReadRequiredBoolean(IConfiguration configuration, string key) =>
        ReadOptionalBoolean(configuration, key, defaultValue: false);

    private static bool ReadOptionalBoolean(
        IConfiguration configuration,
        string key,
        bool defaultValue)
    {
        var value = configuration[key]?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return defaultValue;
        }
        if (!bool.TryParse(value, out var parsed))
        {
            throw new InvalidOperationException($"{key} must be either 'true' or 'false'.");
        }
        return parsed;
    }
}

/// <summary>
/// Evaluates database readiness without changing tenant, account, session, identity, credential,
/// or library state. Configuration is immutable for the lifetime of the process.
/// </summary>
public sealed class MultiUserEnablementService(
    CoreContext db,
    MultiUserEnablementSettings settings,
    OidcSettings oidcSettings,
    IMultiUserIngestReadinessProbe ingestReadiness)
{
    /// <summary>Returns safe readiness state and stable blocker identifiers.</summary>
    public async Task<MultiUserReadiness> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var blockers = settings.ConfigurationBlockers.ToList();
        if (oidcSettings.IsEnabled)
        {
            var hasActiveAccount = await db.UserAccounts
                .AsNoTracking()
                .AnyAsync(
                    account => account.Tenant != null
                        && account.Tenant.DeletionRequestedAtUtc == null,
                    cancellationToken);
            if (!hasActiveAccount)
            {
                blockers.Add("no_active_account");
            }
            else
            {
                // Every live account must have an explicit mapping to the one configured issuer.
                // No profile claim participates, and the query returns only a boolean blocker.
                var hasUnlinkedAccount = await db.UserAccounts
                    .AsNoTracking()
                    .AnyAsync(
                        account => account.Tenant != null
                            && account.Tenant.DeletionRequestedAtUtc == null
                            && !db.ExternalIdentities.Any(identity =>
                                identity.UserAccountId == account.Id
                                && identity.TenantId == account.TenantId
                                && identity.Issuer == oidcSettings.Issuer),
                        cancellationToken);
                if (hasUnlinkedAccount)
                {
                    blockers.Add("active_account_missing_oidc_link");
                }
            }
        }

        try
        {
            var ingest = await ingestReadiness.GetAsync(cancellationToken);
            if (ingest.MultiUserEnabled != settings.Mode.IsEnabled)
            {
                blockers.Add("ingest_multi_user_mode_mismatch");
            }
            if (!ingest.CredentialVaultReady)
            {
                blockers.Add("ingest_credential_vault_not_ready");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Network, authentication, malformed response, and service-key mismatch intentionally
            // collapse to one safe blocker. Neither exception details nor upstream content enter
            // the operator response.
            blockers.Add("ingest_unavailable_or_trust_mismatch");
        }

        return new MultiUserReadiness(
            settings.Mode.IsEnabled,
            oidcSettings.IsEnabled,
            blockers.Count == 0,
            blockers.Distinct().Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Aborts startup when the operator requested enablement before database prerequisites were
    /// satisfied. Keeping the disabled process startable is essential for provisioning/linking.
    /// </summary>
    public async Task ValidateStartupAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.Mode.IsEnabled)
        {
            return;
        }

        var readiness = await EvaluateAsync(cancellationToken);
        if (!readiness.ReadinessSatisfied)
        {
            throw new InvalidOperationException(
                $"Guarded multi-user enablement is blocked: {string.Join(", ", readiness.Blockers)}.");
        }
    }
}

/// <summary>Secret-free guarded-enablement state suitable for operator diagnostics.</summary>
public sealed record MultiUserReadiness(
    bool MultiUserEnabled,
    bool OidcEnabled,
    bool ReadinessSatisfied,
    IReadOnlyList<string> Blockers);

/// <summary>Abstraction for testing the real Core-to-Ingest readiness trust boundary.</summary>
public interface IMultiUserIngestReadinessProbe
{
    /// <summary>Gets safe Ingest process readiness or throws when delegation fails.</summary>
    Task<IngestMultiUserReadinessState> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Production readiness probe backed by the normal delegated Ingest client.</summary>
public sealed class MultiUserIngestReadinessProbe(IngestClient ingestClient)
    : IMultiUserIngestReadinessProbe
{
    /// <inheritdoc />
    public Task<IngestMultiUserReadinessState> GetAsync(
        CancellationToken cancellationToken = default) =>
        ingestClient.GetMultiUserReadinessAsync(cancellationToken);
}
