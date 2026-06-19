namespace GameHub.Core.Data;

/// <summary>
/// Stable identifiers used while GameHub operates in its single-user compatibility mode.
/// </summary>
public static class TenantDefaults
{
    /// <summary>The tenant that owns records created before user accounts are enabled.</summary>
    public static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
}

/// <summary>
/// A GameHub account boundary. Authentication will select the active tenant in a later slice.
/// </summary>
public sealed class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Exposes the tenant selected from trusted server-side identity.</summary>
public interface ITenantContext
{
    Guid TenantId { get; }
}

/// <summary>
/// Keeps existing single-user deployments working until authenticated tenant resolution ships.
/// </summary>
public sealed class DefaultTenantContext : ITenantContext
{
    public Guid TenantId => TenantDefaults.DefaultTenantId;
}

/// <summary>Marks a durable Core record as belonging to exactly one tenant.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}
