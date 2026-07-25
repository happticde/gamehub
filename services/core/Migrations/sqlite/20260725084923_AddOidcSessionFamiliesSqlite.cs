using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite
{
    /// <inheritdoc />
    public partial class AddOidcSessionFamiliesSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.AddColumn<Guid>(
                name: "AccountSessionId",
                table: "RefreshTokenSessions",
                type: "TEXT",
                nullable: true);

            // Legacy refresh rows have no stable family and old access JWTs have no family claim.
            // Revoke them during upgrade so deployment fails closed until a fresh login creates
            // the durable session mapping required by the new bearer validator.
            migrationBuilder.Sql("""
                UPDATE "RefreshTokenSessions"
                SET "RevokedAtUtc" = COALESCE("RevokedAtUtc", CURRENT_TIMESTAMP);
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ExternalIdentities_Id_TenantId_UserAccountId",
                table: "ExternalIdentities",
                columns: new[] { "Id", "TenantId", "UserAccountId" });

            migrationBuilder.CreateTable(
                name: "AccountSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountSessions", x => x.Id);
                    table.UniqueConstraint("AK_AccountSessions_Id_TenantId_UserAccountId", x => new { x.Id, x.TenantId, x.UserAccountId });
                    table.ForeignKey(
                        name: "FK_AccountSessions_UserAccounts_TenantId_UserAccountId",
                        columns: x => new { x.TenantId, x.UserAccountId },
                        principalTable: "UserAccounts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OidcProviderSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionKeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExternalIdentityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AccountSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OidcProviderSessions", x => x.Id);
                    table.CheckConstraint("CK_OidcProviderSessions_Mapping", "(\"ExternalIdentityId\" IS NULL AND \"AccountSessionId\" IS NULL AND \"UserAccountId\" IS NULL AND \"TenantId\" IS NULL AND \"RevokedAtUtc\" IS NOT NULL) OR (\"ExternalIdentityId\" IS NOT NULL AND \"AccountSessionId\" IS NOT NULL AND \"UserAccountId\" IS NOT NULL AND \"TenantId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_OidcProviderSessions_AccountSessions_AccountSessionId_TenantId_UserAccountId",
                        columns: x => new { x.AccountSessionId, x.TenantId, x.UserAccountId },
                        principalTable: "AccountSessions",
                        principalColumns: new[] { "Id", "TenantId", "UserAccountId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OidcProviderSessions_ExternalIdentities_ExternalIdentityId_TenantId_UserAccountId",
                        columns: x => new { x.ExternalIdentityId, x.TenantId, x.UserAccountId },
                        principalTable: "ExternalIdentities",
                        principalColumns: new[] { "Id", "TenantId", "UserAccountId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokenSessions_AccountSessionId",
                table: "RefreshTokenSessions",
                column: "AccountSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountSessions_TenantId_UserAccountId",
                table: "AccountSessions",
                columns: new[] { "TenantId", "UserAccountId" });

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
                columns: new[] { "AccountSessionId", "TenantId", "UserAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OidcProviderSessions_ExternalIdentityId_TenantId_UserAccountId",
                table: "OidcProviderSessions",
                columns: new[] { "ExternalIdentityId", "TenantId", "UserAccountId" });

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
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                return;
            }

            migrationBuilder.DropForeignKey(
                name: "FK_RefreshTokenSessions_AccountSessions_AccountSessionId",
                table: "RefreshTokenSessions");

            migrationBuilder.DropTable(
                name: "OidcProviderSessions");

            migrationBuilder.DropTable(
                name: "AccountSessions");

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
}
