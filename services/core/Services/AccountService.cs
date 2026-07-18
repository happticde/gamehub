using System.Net.Mail;
using GameHub.Core.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Services;

/// <summary>
/// Creates and validates local tenant accounts without exposing password hashes. Database
/// work lives here instead of the controller so password handling follows one audited path.
/// </summary>
public sealed class AccountService
{
    /// <summary>Minimum accepted password length; long passphrases are encouraged.</summary>
    public const int MinimumPasswordLength = 12;

    /// <summary>Upper bound that limits password-hashing work and abusive request sizes.</summary>
    public const int MaximumPasswordLength = 128;

    private readonly CoreContext _db;
    private readonly IPasswordHasher<UserAccount> _passwordHasher;
    private readonly TimeProvider _timeProvider;

    public AccountService(
        CoreContext db,
        IPasswordHasher<UserAccount> passwordHasher,
        TimeProvider timeProvider)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Creates the first account for an existing tenant. The tenant ID is supplied by the
    /// trusted API-key claim through AuthController, not by the request JSON.
    /// </summary>
    public async Task<AccountCreationResult> BootstrapAsync(
        Guid tenantId,
        string? email,
        string? password,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        // Store both a readable canonical address and a normalized comparison value. This
        // prevents Owner@example.com and owner@example.com from creating separate accounts.
        if (!TryNormalizeEmail(email, out var canonicalEmail, out var normalizedEmail))
        {
            return AccountCreationResult.Failed("invalid_email", "A valid email address is required.");
        }

        // We intentionally use length rules rather than brittle "must contain a symbol"
        // composition rules. The PasswordHasher handles salt and computational hardening.
        if (password is null || password.Length is < MinimumPasswordLength or > MaximumPasswordLength)
        {
            return AccountCreationResult.Failed(
                "invalid_password",
                $"Password length must be between {MinimumPasswordLength} and {MaximumPasswordLength} characters.");
        }

        var canonicalDisplayName = displayName?.Trim();
        if (string.IsNullOrEmpty(canonicalDisplayName) || canonicalDisplayName.Length > 100)
        {
            return AccountCreationResult.Failed(
                "invalid_display_name",
                "Display name length must be between 1 and 100 characters.");
        }

        // A valid foreign key is not enough for a friendly API error, so check the trusted
        // tenant first. This also catches a misconfigured Authentication:TenantId value.
        if (!await _db.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken))
        {
            return AccountCreationResult.Failed("tenant_not_found", "The configured tenant does not exist.");
        }

        // This slice models one owner account per tenant. Both checks are repeated as unique
        // database indexes, because application checks alone cannot prevent concurrent races.
        if (await _db.UserAccounts.AnyAsync(account => account.TenantId == tenantId, cancellationToken))
        {
            return AccountCreationResult.Failed(
                "account_exists",
                "An account already exists for this tenant.");
        }

        if (await _db.UserAccounts.AnyAsync(
                account => account.NormalizedEmail == normalizedEmail,
                cancellationToken))
        {
            return AccountCreationResult.Failed(
                "email_exists",
                "An account already exists for this email address.");
        }

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AccountType = UserAccountType.Local,
            Email = canonicalEmail,
            NormalizedEmail = normalizedEmail,
            DisplayName = canonicalDisplayName,
            CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime
        };
        // HashPassword generates a random salt and stores algorithm/version metadata in the
        // encoded hash. The original password cannot be recovered from this value.
        account.PasswordHash = _passwordHasher.HashPassword(account, password);

        _db.UserAccounts.Add(account);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        // Another bootstrap request may win after the AnyAsync checks but before SaveChanges.
        // Unique indexes are the final authority; translate their failure into a safe conflict
        // without exposing provider-specific SQL details.
        catch (DbUpdateException)
        {
            return AccountCreationResult.Failed(
                "account_conflict",
                "The account could not be created because it already exists.");
        }

        return AccountCreationResult.Succeeded(account);
    }

    /// <summary>
    /// Creates a new tenant boundary and its first owner account in one SaveChanges call.
    /// EF wraps that call in a transaction, so a foreign-key or uniqueness failure persists
    /// neither object. The caller cannot choose TenantId or enable unsafe platform sync.
    /// </summary>
    public async Task<TenantProvisioningResult> ProvisionTenantAsync(
        string? tenantName,
        string? email,
        string? password,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var canonicalTenantName = tenantName?.Trim();
        if (string.IsNullOrEmpty(canonicalTenantName) || canonicalTenantName.Length > 200)
        {
            return TenantProvisioningResult.Failed(
                "invalid_tenant_name",
                "Tenant name length must be between 1 and 200 characters.");
        }

        var validation = ValidateAccountInput(email, password, displayName);
        if (validation.ErrorCode is not null)
        {
            return TenantProvisioningResult.Failed(validation.ErrorCode, validation.ErrorMessage!);
        }

        if (await _db.UserAccounts.AnyAsync(
                account => account.NormalizedEmail == validation.NormalizedEmail,
                cancellationToken))
        {
            return TenantProvisioningResult.Failed(
                "email_exists",
                "An account already exists for this email address.");
        }

        var createdAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = canonicalTenantName,
            // Provisioning never activates background retrieval. The server—not the request—keeps
            // sync off until this exact tenant has a freshly verified supported vault credential.
            PlatformSyncEnabled = false,
            CreatedAtUtc = createdAtUtc
        };
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Tenant = tenant,
            AccountType = UserAccountType.Local,
            Email = validation.CanonicalEmail!,
            NormalizedEmail = validation.NormalizedEmail!,
            DisplayName = validation.CanonicalDisplayName!,
            CreatedAtUtc = createdAtUtc
        };
        account.PasswordHash = _passwordHasher.HashPassword(account, password!);

        // Adding the account also tracks its new Tenant navigation. A single SaveChanges is
        // the transaction boundary: both rows commit together or both are rolled back.
        _db.UserAccounts.Add(account);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return TenantProvisioningResult.Failed(
                "account_conflict",
                "The tenant could not be provisioned because the account already exists.");
        }

        return TenantProvisioningResult.Succeeded(tenant, account);
    }

    /// <summary>
    /// Validates local credentials and records a successful login. Null is deliberately used
    /// for every failure so the controller returns the same response for an unknown email and
    /// a wrong password, avoiding easy account enumeration.
    /// </summary>
    public async Task<UserAccount?> AuthenticateAsync(
        string? email,
        string? password,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeEmail(email, out _, out var normalizedEmail)
            || string.IsNullOrEmpty(password)
            || password.Length > MaximumPasswordLength)
        {
            return null;
        }

        // UserAccount is intentionally not behind the tenant query filter: at login time no
        // tenant is trusted yet. The globally unique normalized email locates an account;
        // only successful password verification allows its TenantId into a signed token.
        var account = await _db.UserAccounts
            .Include(candidate => candidate.Tenant)
            .SingleOrDefaultAsync(
                candidate => candidate.AccountType == UserAccountType.Local
                    && candidate.NormalizedEmail == normalizedEmail,
                cancellationToken);
        if (account is null)
        {
            return null;
        }

        // VerifyHashedPassword extracts the salt and algorithm parameters from PasswordHash,
        // hashes the supplied password the same way, and compares the results safely.
        if (string.IsNullOrEmpty(account.PasswordHash))
        {
            return null;
        }

        var verification = _passwordHasher.VerifyHashedPassword(account, account.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            return null;
        }

        // ASP.NET can signal that an older, valid hash should be upgraded. Rehashing here
        // transparently improves stored credentials during a normal successful login.
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = _passwordHasher.HashPassword(account, password);
        }

        // Record activity only after verification. TimeProvider keeps this deterministic in
        // tests and avoids mixing local server time with persisted UTC timestamps.
        account.LastLoginAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);
        return account;
    }

    /// <summary>
    /// Replaces an authenticated account password only after verifying the current password.
    /// Every access and refresh session is invalidated in the same transaction as the new hash.
    /// </summary>
    public async Task<PasswordChangeResult> ChangePasswordAsync(
        Guid accountId,
        Guid tenantId,
        string? currentPassword,
        string? newPassword,
        CancellationToken cancellationToken = default)
    {
        if (newPassword is null
            || newPassword.Length is < MinimumPasswordLength or > MaximumPasswordLength)
        {
            return PasswordChangeResult.Failed(
                "invalid_new_password",
                $"New password length must be between {MinimumPasswordLength} and {MaximumPasswordLength} characters.");
        }

        var account = await _db.UserAccounts.SingleOrDefaultAsync(
            candidate => candidate.Id == accountId && candidate.TenantId == tenantId,
            cancellationToken);
        if (account is null)
        {
            return PasswordChangeResult.Failed(
                "account_identity_mismatch",
                "The authenticated account no longer exists.");
        }

        if (account.AccountType != UserAccountType.Local)
        {
            return PasswordChangeResult.Failed(
                "local_password_not_available",
                "Local password changes are not available for this account.");
        }

        if (!VerifyPassword(account, currentPassword))
        {
            return PasswordChangeResult.Failed(
                "invalid_current_password",
                "Current password is invalid.");
        }

        if (VerifyPassword(account, newPassword))
        {
            return PasswordChangeResult.Failed(
                "invalid_new_password",
                "New password must differ from the current password.");
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        account.PasswordHash = _passwordHasher.HashPassword(account, newPassword);
        account.SessionVersion = checked(account.SessionVersion + 1);
        await RevokeAllSessionsForAccountAsync(account.Id, nowUtc, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PasswordChangeResult.Succeeded;
    }

    /// <summary>
    /// Invalidates every access and refresh session for the exact signed account/tenant pair.
    /// The account lookup prevents a valid Tenant A principal from selecting Tenant B.
    /// </summary>
    public async Task<bool> RevokeAllSessionsAsync(
        Guid accountId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var account = await _db.UserAccounts.SingleOrDefaultAsync(
            candidate => candidate.Id == accountId && candidate.TenantId == tenantId,
            cancellationToken);
        if (account is null)
        {
            return false;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        account.SessionVersion = checked(account.SessionVersion + 1);
        await RevokeAllSessionsForAccountAsync(
            accountId,
            _timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Confirms destructive deletion and persists its fail-closed retry state before Core calls
    /// Ingest. Repeated preparation is safe; a missing account is accepted only when neither
    /// half of the signed account/tenant pair now belongs to another live identity.
    /// </summary>
    public async Task<AccountDeletionPreparationResult> PrepareDeletionAsync(
        Guid accountId,
        Guid tenantId,
        string? currentPassword,
        CancellationToken cancellationToken = default,
        AccountDeletionAuthorization authorization = AccountDeletionAuthorization.LocalPassword)
    {
        var account = await _db.UserAccounts
            .Include(candidate => candidate.Tenant)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == accountId && candidate.TenantId == tenantId,
                cancellationToken);
        if (account is null)
        {
            var identityStillInUse = await _db.UserAccounts.AnyAsync(
                candidate => candidate.Id == accountId || candidate.TenantId == tenantId,
                cancellationToken);
            return identityStillInUse
                ? AccountDeletionPreparationResult.IdentityMismatch
                : AccountDeletionPreparationResult.AlreadyDeleted;
        }

        if (account.Tenant is null)
        {
            return AccountDeletionPreparationResult.IdentityMismatch;
        }

        var deletionConfirmed = account.AccountType switch
        {
            UserAccountType.Local => authorization == AccountDeletionAuthorization.LocalPassword
                && VerifyPassword(account, currentPassword),
            UserAccountType.ExternalOidc =>
                authorization == AccountDeletionAuthorization.FreshExternalOidc,
            _ => false
        };
        if (!deletionConfirmed)
        {
            return AccountDeletionPreparationResult.InvalidCurrentPassword;
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        account.Tenant.PlatformSyncEnabled = false;
        account.Tenant.DeletionRequestedAtUtc ??= nowUtc;
        account.SessionVersion = checked(account.SessionVersion + 1);
        await RevokeAllSessionsForAccountAsync(account.Id, nowUtc, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AccountDeletionPreparationResult.Prepared;
    }

    private bool VerifyPassword(UserAccount account, string? password)
    {
        if (account.AccountType != UserAccountType.Local
            || string.IsNullOrEmpty(account.PasswordHash)
            || string.IsNullOrEmpty(password)
            || password.Length > MaximumPasswordLength)
        {
            return false;
        }

        return _passwordHasher.VerifyHashedPassword(account, account.PasswordHash, password)
            != PasswordVerificationResult.Failed;
    }

    private Task<int> RevokeAllSessionsForAccountAsync(
        Guid accountId,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken) =>
        _db.RefreshTokenSessions
            .Where(session => session.UserAccountId == accountId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAtUtc, revokedAtUtc),
                cancellationToken);

    /// <summary>
    /// Validates a plain mailbox address and produces its case-insensitive lookup form.
    /// Display-address syntax such as "Owner &lt;owner@example.com&gt;" is rejected because the
    /// API contract expects an address only.
    /// </summary>
    private static bool TryNormalizeEmail(
        string? email,
        out string canonicalEmail,
        out string normalizedEmail)
    {
        canonicalEmail = email?.Trim() ?? "";
        normalizedEmail = "";
        if (canonicalEmail.Length is 0 or > 320
            || !MailAddress.TryCreate(canonicalEmail, out var parsed)
            || !string.Equals(parsed.Address, canonicalEmail, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        canonicalEmail = parsed.Address;
        normalizedEmail = canonicalEmail.ToUpperInvariant();
        return true;
    }

    private static AccountInputValidation ValidateAccountInput(
        string? email,
        string? password,
        string? displayName)
    {
        if (!TryNormalizeEmail(email, out var canonicalEmail, out var normalizedEmail))
        {
            return AccountInputValidation.Failed("invalid_email", "A valid email address is required.");
        }

        if (password is null || password.Length is < MinimumPasswordLength or > MaximumPasswordLength)
        {
            return AccountInputValidation.Failed(
                "invalid_password",
                $"Password length must be between {MinimumPasswordLength} and {MaximumPasswordLength} characters.");
        }

        var canonicalDisplayName = displayName?.Trim();
        if (string.IsNullOrEmpty(canonicalDisplayName) || canonicalDisplayName.Length > 100)
        {
            return AccountInputValidation.Failed(
                "invalid_display_name",
                "Display name length must be between 1 and 100 characters.");
        }

        return new AccountInputValidation(
            canonicalEmail,
            normalizedEmail,
            canonicalDisplayName,
            null,
            null);
    }

    private sealed record AccountInputValidation(
        string? CanonicalEmail,
        string? NormalizedEmail,
        string? CanonicalDisplayName,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static AccountInputValidation Failed(string code, string message) =>
            new(null, null, null, code, message);
    }
}

/// <summary>
/// Result of attempting to bootstrap an account. Expected validation/conflict outcomes are
/// values rather than exceptions, allowing AuthController to map them to stable HTTP errors.
/// </summary>
public sealed record AccountCreationResult(
    UserAccount? Account,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>True only when a new persisted account is available.</summary>
    public bool Created => Account is not null;

    /// <summary>Creates a successful result without an error payload.</summary>
    public static AccountCreationResult Succeeded(UserAccount account) => new(account, null, null);

    /// <summary>Creates an expected failure result safe for controller mapping.</summary>
    public static AccountCreationResult Failed(string code, string message) => new(null, code, message);
}

/// <summary>Result of atomically creating a tenant and its first owner account.</summary>
public sealed record TenantProvisioningResult(
    Tenant? Tenant,
    UserAccount? Account,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Created => Tenant is not null && Account is not null;

    public static TenantProvisioningResult Succeeded(Tenant tenant, UserAccount account) =>
        new(tenant, account, null, null);

    public static TenantProvisioningResult Failed(string code, string message) =>
        new(null, null, code, message);
}

/// <summary>Expected result of an authenticated password-change attempt.</summary>
public sealed record PasswordChangeResult(string? ErrorCode, string? ErrorMessage)
{
    public bool Changed => ErrorCode is null;

    public static PasswordChangeResult Succeeded { get; } = new(null, null);

    public static PasswordChangeResult Failed(string code, string message) => new(code, message);
}

/// <summary>Security decision made before a cross-service account deletion starts.</summary>
public enum AccountDeletionPreparationResult
{
    Prepared,
    AlreadyDeleted,
    InvalidCurrentPassword,
    IdentityMismatch
}

/// <summary>Server-established proof accepted when account deletion enters pending state.</summary>
public enum AccountDeletionAuthorization
{
    /// <summary>A local account supplied and verified its current password.</summary>
    LocalPassword,

    /// <summary>An external account presented a fresh exact OIDC reauthentication proof.</summary>
    FreshExternalOidc
}
