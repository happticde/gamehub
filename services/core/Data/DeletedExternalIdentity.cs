namespace GameHub.Core.Data;

/// <summary>
/// Durable denial record for an external identity whose GameHub account was deleted. JIT checks
/// this exact pair before provisioning so account deletion cannot silently become registration.
/// </summary>
public sealed class DeletedExternalIdentity
{
    /// <summary>Internal identifier with no provider meaning.</summary>
    public Guid Id { get; set; }

    /// <summary>Exact case-sensitive issuer previously linked to the deleted account.</summary>
    public string Issuer { get; set; } = "";

    /// <summary>Exact provider-stable subject previously linked to the deleted account.</summary>
    public string Subject { get; set; } = "";

    /// <summary>UTC time at which Core committed the account deletion.</summary>
    public DateTime DeletedAtUtc { get; set; }
}
