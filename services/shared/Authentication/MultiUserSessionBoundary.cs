namespace GameHub.Shared.Authentication;

/// <summary>Pure request boundary for the locally authenticated OIDC-link bootstrap token.</summary>
public static class MultiUserSessionBoundary
{
    /// <summary>
    /// Permits exactly the Core linking callback. Prefixes, alternate methods, query values,
    /// headers, and browser state cannot widen the signed token's purpose.
    /// </summary>
    public static bool AllowsOidcLinkOnlyRequest(string method, string path) =>
        string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
        && string.Equals(path, "/api/auth/oidc/link", StringComparison.Ordinal);

    /// <summary>Permits only the existing idempotent account-deletion retry endpoint.</summary>
    public static bool AllowsAccountDeletionOnlyRequest(string method, string path) =>
        string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
        && string.Equals(path, "/api/auth/account", StringComparison.Ordinal);
}
