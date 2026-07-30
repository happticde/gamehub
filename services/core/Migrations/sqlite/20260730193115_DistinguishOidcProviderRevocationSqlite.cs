using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite
{
    /// <inheritdoc />
    public partial class DistinguishOidcProviderRevocationSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.DropCheckConstraint(
                name: "CK_OidcProviderSessions_Mapping",
                table: "OidcProviderSessions");

            migrationBuilder.AddColumn<DateTime>(
                name: "ReissueBlockedAtUtc",
                table: "OidcProviderSessions",
                type: "TEXT",
                nullable: true);

            // Older rows did not record whether revocation came from GameHub or the provider.
            // Preserve the former fail-closed behavior by treating every historical revocation
            // as provider-authoritative. New local revocations leave this column null.
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
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
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
}
