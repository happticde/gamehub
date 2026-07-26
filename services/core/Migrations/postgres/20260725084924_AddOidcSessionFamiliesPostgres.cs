using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.postgres;

/// <inheritdoc />
[DbContext(typeof(CoreContext))]
[Migration("20260725084924_AddOidcSessionFamiliesPostgres")]
public sealed class AddOidcSessionFamiliesPostgres : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.AddColumn<Guid>(
            name: "AccountSessionId",
            table: "RefreshTokenSessions",
            type: "uuid",
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE "RefreshTokenSessions"
            SET "RevokedAtUtc" = COALESCE("RevokedAtUtc", CURRENT_TIMESTAMP);
            """);

        migrationBuilder.AddUniqueConstraint(
            name: "AK_ExternalIdentities_Id_TenantId_UserAccountId",
            table: "ExternalIdentities",
            columns: ["Id", "TenantId", "UserAccountId"]);

        migrationBuilder.CreateTable(
            name: "AccountSessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: false),
                RevokedAtUtc = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AccountSessions", row => row.Id);
                table.UniqueConstraint(
                    "AK_AccountSessions_Id_TenantId_UserAccountId",
                    row => new { row.Id, row.TenantId, row.UserAccountId });
                table.ForeignKey(
                    name: "FK_AccountSessions_UserAccounts_TenantId_UserAccountId",
                    columns: row => new { row.TenantId, row.UserAccountId },
                    principalTable: "UserAccounts",
                    principalColumns: ["TenantId", "Id"],
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "OidcProviderSessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                SessionKeyHash = table.Column<string>(
                    type: "character varying(64)",
                    maxLength: 64,
                    nullable: false),
                ExternalIdentityId = table.Column<Guid>(type: "uuid", nullable: true),
                AccountSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                UserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: false),
                RevokedAtUtc = table.Column<DateTime>(
                    type: "timestamp with time zone",
                    nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OidcProviderSessions", row => row.Id);
                table.CheckConstraint(
                    "CK_OidcProviderSessions_Mapping",
                    "(\"ExternalIdentityId\" IS NULL AND \"AccountSessionId\" IS NULL AND \"UserAccountId\" IS NULL AND \"TenantId\" IS NULL AND \"RevokedAtUtc\" IS NOT NULL) OR (\"ExternalIdentityId\" IS NOT NULL AND \"AccountSessionId\" IS NOT NULL AND \"UserAccountId\" IS NOT NULL AND \"TenantId\" IS NOT NULL)");
                table.ForeignKey(
                    name: "FK_OidcProviderSessions_AccountSessions_AccountSessionId_TenantId_UserAccountId",
                    columns: row => new
                    {
                        row.AccountSessionId,
                        row.TenantId,
                        row.UserAccountId
                    },
                    principalTable: "AccountSessions",
                    principalColumns: ["Id", "TenantId", "UserAccountId"],
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_OidcProviderSessions_ExternalIdentities_ExternalIdentityId_TenantId_UserAccountId",
                    columns: row => new
                    {
                        row.ExternalIdentityId,
                        row.TenantId,
                        row.UserAccountId
                    },
                    principalTable: "ExternalIdentities",
                    principalColumns: ["Id", "TenantId", "UserAccountId"],
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RefreshTokenSessions_AccountSessionId",
            table: "RefreshTokenSessions",
            column: "AccountSessionId");

        migrationBuilder.CreateIndex(
            name: "IX_AccountSessions_TenantId_UserAccountId",
            table: "AccountSessions",
            columns: ["TenantId", "UserAccountId"]);

        migrationBuilder.CreateIndex(
            name: "IX_AccountSessions_UserAccountId",
            table: "AccountSessions",
            column: "UserAccountId");

        migrationBuilder.CreateIndex(
            name: "IX_OidcProviderSessions_AccountSessionId",
            table: "OidcProviderSessions",
            column: "AccountSessionId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OidcProviderSessions_AccountSessionId_TenantId_UserAccountId",
            table: "OidcProviderSessions",
            columns: ["AccountSessionId", "TenantId", "UserAccountId"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OidcProviderSessions_ExternalIdentityId_TenantId_UserAccountId",
            table: "OidcProviderSessions",
            columns: ["ExternalIdentityId", "TenantId", "UserAccountId"]);

        migrationBuilder.CreateIndex(
            name: "IX_OidcProviderSessions_SessionKeyHash",
            table: "OidcProviderSessions",
            column: "SessionKeyHash",
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_RefreshTokenSessions_AccountSessions_AccountSessionId",
            table: "RefreshTokenSessions",
            column: "AccountSessionId",
            principalTable: "AccountSessions",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.DropForeignKey(
            name: "FK_RefreshTokenSessions_AccountSessions_AccountSessionId",
            table: "RefreshTokenSessions");
        migrationBuilder.DropTable(name: "OidcProviderSessions");
        migrationBuilder.DropTable(name: "AccountSessions");
        migrationBuilder.DropIndex(
            name: "IX_RefreshTokenSessions_AccountSessionId",
            table: "RefreshTokenSessions");
        migrationBuilder.DropUniqueConstraint(
            name: "AK_ExternalIdentities_Id_TenantId_UserAccountId",
            table: "ExternalIdentities");
        migrationBuilder.DropColumn(
            name: "AccountSessionId",
            table: "RefreshTokenSessions");
    }
}
