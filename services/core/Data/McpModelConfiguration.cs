using Microsoft.EntityFrameworkCore;

namespace GameHub.Core.Data;

/// <summary>Connection ownership and immutable-consent persistence boundaries.</summary>
internal static class McpModelConfiguration
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        // A unique index (rather than an EF alternate key) preserves nullable tombstone owners.
        // Migrations use it as the principal of the grant's composite provider foreign key.
        modelBuilder.Entity<OidcProviderSession>()
            .HasIndex(row => new { row.Id, row.TenantId, row.UserAccountId }).IsUnique();
        modelBuilder.Entity<McpProtocolAuthorization>(entity =>
        {
            entity.HasIndex(row => row.AccountSessionId).IsUnique();
            entity.HasAlternateKey(row => new
            {
                row.Id, row.TenantId, row.UserAccountId, row.AccountSessionId
            });
            entity.HasOne<AccountSession>().WithMany()
                .HasForeignKey(row => new { row.AccountSessionId, row.TenantId, row.UserAccountId })
                .HasPrincipalKey(row => new { row.Id, row.TenantId, row.UserAccountId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<McpGrant>(entity =>
        {
            entity.Property(row => row.ProtocolAuthorizationId).HasMaxLength(100);
            entity.Property(row => row.ClientId).HasMaxLength(100).IsRequired();
            entity.Property(row => row.Resource).HasMaxLength(2048).IsRequired();
            entity.Property(row => row.Scopes).HasMaxLength(64).IsRequired();
            entity.HasIndex(row => row.AccountSessionId).IsUnique();
            entity.HasIndex(row => row.ProtocolAuthorizationId).IsUnique();
            entity.HasIndex(row => new { row.TenantId, row.UserAccountId });
            entity.HasOne(row => row.AccountSession).WithMany()
                .HasForeignKey(row => new { row.AccountSessionId, row.TenantId, row.UserAccountId })
                .HasPrincipalKey(row => new { row.Id, row.TenantId, row.UserAccountId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(row => row.ProtocolAuthorization).WithOne()
                .HasForeignKey<McpGrant>(row => new
                {
                    row.ProtocolAuthorizationId, row.TenantId, row.UserAccountId, row.AccountSessionId
                })
                .HasPrincipalKey<McpProtocolAuthorization>(row => new
                {
                    row.Id, row.TenantId, row.UserAccountId, row.AccountSessionId
                })
                .OnDelete(DeleteBehavior.Restrict);
            // Provider owner columns are nullable for anonymous logout tombstones. EF alternate
            // keys cannot remain nullable. Provider-specific migration constraints therefore
            // enforce this reference's composite owner without changing the tombstone model.
            entity.HasOne(row => row.OidcProviderSession).WithMany()
                .HasForeignKey(row => row.OidcProviderSessionId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_McpGrants_Scopes",
                    "\"Scopes\" IN ('library:read', 'library:read tracking:write')");
                table.HasCheckConstraint("CK_McpGrants_Lifetime",
                    "\"ExpiresAtUtc\" > \"CreatedAtUtc\" AND \"ApprovedSessionVersion\" >= 0 AND (\"RevokedAtUtc\" IS NULL OR \"RevokedAtUtc\" >= \"CreatedAtUtc\")");
                table.HasCheckConstraint("CK_McpGrants_ClientResource",
                    "length(\"ClientId\") BETWEEN 1 AND 100 AND length(\"Resource\") BETWEEN 1 AND 2048 AND \"Resource\" LIKE 'https://%' AND \"Resource\" NOT LIKE '% %' AND \"Resource\" NOT LIKE '%#%'");
            });
        });
    }
}
