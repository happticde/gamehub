namespace GameHub.Core.Models;

/// <summary>
/// Secret-free connection view for the later account-management API. Ownership is resolved from
/// authenticated claims; provider identifiers and protocol credentials are never exposed.
/// </summary>
public sealed record McpConnectionDto(
    Guid Id,
    string ClientId,
    string Resource,
    IReadOnlyList<string> Scopes,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? RevokedAtUtc);
