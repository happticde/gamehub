using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.postgres;

/// <inheritdoc />
[DbContext(typeof(CoreContext))]
[Migration("20260730193116_DistinguishOidcProviderRevocationPostgres")]
public sealed class DistinguishOidcProviderRevocationPostgres : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.DropCheckConstraint(
            name: "CK_OidcProviderSessions_Mapping",
            table: "OidcProviderSessions");

        migrationBuilder.AddColumn<DateTime>(
            name: "ReissueBlockedAtUtc",
            table: "OidcProviderSessions",
            type: "timestamp with time zone",
            nullable: true);

        // Historical rows cannot reveal whether GameHub or the provider initiated revocation.
        // Treat them as provider-authoritative so an upgrade cannot reopen a logged-out sid.
        migrationBuilder.Sql("""
            UPDATE "OidcProviderSessions"
            SET "ReissueBlockedAtUtc" = "RevokedAtUtc"
            WHERE "RevokedAtUtc" IS NOT NULL;
            """);

        migrationBuilder.AddCheckConstraint(
            name: "CK_OidcProviderSessions_Mapping",
            table: "OidcProviderSessions",
            sql: "((\"ExternalIdentityId\" IS NULL AND \"AccountSessionId\" IS NULL AND \"UserAccountId\" IS NULL AND \"TenantId\" IS NULL AND \"RevokedAtUtc\" IS NOT NULL AND \"ReissueBlockedAtUtc\" IS NOT NULL) OR (\"ExternalIdentityId\" IS NOT NULL AND \"AccountSessionId\" IS NOT NULL AND \"UserAccountId\" IS NOT NULL AND \"TenantId\" IS NOT NULL)) AND (\"ReissueBlockedAtUtc\" IS NULL OR \"RevokedAtUtc\" IS NOT NULL)");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.DropCheckConstraint(
            name: "CK_OidcProviderSessions_Mapping",
            table: "OidcProviderSessions");
        migrationBuilder.DropColumn(
            name: "ReissueBlockedAtUtc",
            table: "OidcProviderSessions");
        migrationBuilder.AddCheckConstraint(
            name: "CK_OidcProviderSessions_Mapping",
            table: "OidcProviderSessions",
            sql: "(\"ExternalIdentityId\" IS NULL AND \"AccountSessionId\" IS NULL AND \"UserAccountId\" IS NULL AND \"TenantId\" IS NULL AND \"RevokedAtUtc\" IS NOT NULL) OR (\"ExternalIdentityId\" IS NOT NULL AND \"AccountSessionId\" IS NOT NULL AND \"UserAccountId\" IS NOT NULL AND \"TenantId\" IS NOT NULL)");
    }
}
