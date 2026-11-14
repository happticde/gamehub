using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite
{
    /// <inheritdoc />
    public partial class AddMcpGrantContractsSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite") return;

            // Nullable owners must remain nullable for provider logout tombstones. A unique
            // index can back this composite FK without an EF alternate key forcing NOT NULL.
            migrationBuilder.CreateIndex(
                name: "IX_OidcProviderSessions_Id_TenantId_UserAccountId",
                table: "OidcProviderSessions",
                columns: new[] { "Id", "TenantId", "UserAccountId" },
                unique: true);

            migrationBuilder.CreateTable(
                name: "OpenIddictApplications",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ApplicationType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ClientSecret = table.Column<string>(type: "TEXT", nullable: true),
                    ClientType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ConsentType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayNames = table.Column<string>(type: "TEXT", nullable: true),
                    JsonWebKeySet = table.Column<string>(type: "TEXT", nullable: true),
                    Permissions = table.Column<string>(type: "TEXT", nullable: true),
                    PostLogoutRedirectUris = table.Column<string>(type: "TEXT", nullable: true),
                    Properties = table.Column<string>(type: "TEXT", nullable: true),
                    RedirectUris = table.Column<string>(type: "TEXT", nullable: true),
                    Requirements = table.Column<string>(type: "TEXT", nullable: true),
                    Settings = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictScopes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Descriptions = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayNames = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Properties = table.Column<string>(type: "TEXT", nullable: true),
                    Resources = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictScopes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictAuthorizations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Properties = table.Column<string>(type: "TEXT", nullable: true),
                    Scopes = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictAuthorizations", x => x.Id);
                    table.UniqueConstraint("AK_OpenIddictAuthorizations_Id_TenantId_UserAccountId_AccountSessionId", x => new { x.Id, x.TenantId, x.UserAccountId, x.AccountSessionId });
                    table.ForeignKey(
                        name: "FK_OpenIddictAuthorizations_AccountSessions_AccountSessionId_TenantId_UserAccountId",
                        columns: x => new { x.AccountSessionId, x.TenantId, x.UserAccountId },
                        principalTable: "AccountSessions",
                        principalColumns: new[] { "Id", "TenantId", "UserAccountId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpenIddictAuthorizations_OpenIddictApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictTokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", nullable: true),
                    AuthorizationId = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpirationDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: true),
                    Properties = table.Column<string>(type: "TEXT", nullable: true),
                    RedemptionDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReferenceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId",
                        column: x => x.AuthorizationId,
                        principalTable: "OpenIddictAuthorizations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "McpGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProtocolAuthorizationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    OidcProviderSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Resource = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Scopes = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ApprovedSessionVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpGrants", x => x.Id);
                    table.CheckConstraint("CK_McpGrants_ClientResource", "length(\"ClientId\") BETWEEN 1 AND 100 AND length(\"Resource\") BETWEEN 1 AND 2048 AND \"Resource\" LIKE 'https://%' AND \"Resource\" NOT LIKE '% %' AND \"Resource\" NOT LIKE '%#%'");
                    table.CheckConstraint("CK_McpGrants_Lifetime", "\"ExpiresAtUtc\" > \"CreatedAtUtc\" AND \"ApprovedSessionVersion\" >= 0 AND (\"RevokedAtUtc\" IS NULL OR \"RevokedAtUtc\" >= \"CreatedAtUtc\")");
                    table.CheckConstraint("CK_McpGrants_Scopes", "\"Scopes\" IN ('library:read', 'library:read tracking:write')");
                    table.ForeignKey(
                        name: "FK_McpGrants_AccountSessions_AccountSessionId_TenantId_UserAccountId",
                        columns: x => new { x.AccountSessionId, x.TenantId, x.UserAccountId },
                        principalTable: "AccountSessions",
                        principalColumns: new[] { "Id", "TenantId", "UserAccountId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpGrants_OidcProviderSessions_OidcProviderSessionId",
                        columns: x => new { x.OidcProviderSessionId, x.TenantId, x.UserAccountId },
                        principalTable: "OidcProviderSessions",
                        principalColumns: new[] { "Id", "TenantId", "UserAccountId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpGrants_OpenIddictAuthorizations_ProtocolAuthorizationId_TenantId_UserAccountId_AccountSessionId",
                        columns: x => new { x.ProtocolAuthorizationId, x.TenantId, x.UserAccountId, x.AccountSessionId },
                        principalTable: "OpenIddictAuthorizations",
                        principalColumns: new[] { "Id", "TenantId", "UserAccountId", "AccountSessionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpGrants_AccountSessionId",
                table: "McpGrants",
                column: "AccountSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpGrants_AccountSessionId_TenantId_UserAccountId",
                table: "McpGrants",
                columns: new[] { "AccountSessionId", "TenantId", "UserAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_McpGrants_OidcProviderSessionId",
                table: "McpGrants",
                column: "OidcProviderSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_McpGrants_ProtocolAuthorizationId",
                table: "McpGrants",
                column: "ProtocolAuthorizationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpGrants_ProtocolAuthorizationId_TenantId_UserAccountId_AccountSessionId",
                table: "McpGrants",
                columns: new[] { "ProtocolAuthorizationId", "TenantId", "UserAccountId", "AccountSessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpGrants_TenantId_UserAccountId",
                table: "McpGrants",
                columns: new[] { "TenantId", "UserAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictApplications_ClientId",
                table: "OpenIddictApplications",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictAuthorizations_AccountSessionId",
                table: "OpenIddictAuthorizations",
                column: "AccountSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictAuthorizations_AccountSessionId_TenantId_UserAccountId",
                table: "OpenIddictAuthorizations",
                columns: new[] { "AccountSessionId", "TenantId", "UserAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictAuthorizations_ApplicationId_Status_Subject_Type",
                table: "OpenIddictAuthorizations",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictScopes_Name",
                table: "OpenIddictScopes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ApplicationId_Status_Subject_Type",
                table: "OpenIddictTokens",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_AuthorizationId",
                table: "OpenIddictTokens",
                column: "AuthorizationId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ReferenceId",
                table: "OpenIddictTokens",
                column: "ReferenceId",
                unique: true);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_McpGrants_IndependentFamily" BEFORE INSERT ON "McpGrants"
                BEGIN
                    SELECT RAISE(ABORT, 'MCP requires an independent family')
                    WHERE EXISTS (SELECT 1 FROM "OidcProviderSessions" WHERE "AccountSessionId" = NEW."AccountSessionId")
                       OR EXISTS (SELECT 1 FROM "RefreshTokenSessions" WHERE "AccountSessionId" = NEW."AccountSessionId");
                END;
                CREATE TRIGGER "TR_McpGrants_ImmutableConsent" BEFORE UPDATE ON "McpGrants"
                WHEN OLD."Id" IS NOT NEW."Id" OR OLD."TenantId" IS NOT NEW."TenantId"
                  OR OLD."UserAccountId" IS NOT NEW."UserAccountId"
                  OR OLD."AccountSessionId" IS NOT NEW."AccountSessionId"
                  OR OLD."ProtocolAuthorizationId" IS NOT NEW."ProtocolAuthorizationId"
                  OR OLD."OidcProviderSessionId" IS NOT NEW."OidcProviderSessionId"
                  OR OLD."ClientId" IS NOT NEW."ClientId" OR OLD."Resource" IS NOT NEW."Resource"
                  OR OLD."Scopes" IS NOT NEW."Scopes"
                  OR OLD."ApprovedSessionVersion" IS NOT NEW."ApprovedSessionVersion"
                  OR OLD."CreatedAtUtc" IS NOT NEW."CreatedAtUtc" OR OLD."ExpiresAtUtc" IS NOT NEW."ExpiresAtUtc"
                  OR (OLD."RevokedAtUtc" IS NOT NULL AND OLD."RevokedAtUtc" IS NOT NEW."RevokedAtUtc")
                BEGIN
                    SELECT RAISE(ABORT, 'MCP consent is immutable');
                END;
                """);
            foreach (var table in new[] { "OidcProviderSessions", "RefreshTokenSessions" })
            {
                foreach (var operation in new[] { "INSERT", "UPDATE" })
                {
                    migrationBuilder.Sql($"""
                        CREATE TRIGGER "TR_{table}_NoMcpFamily_{operation}" BEFORE {operation} ON "{table}"
                        WHEN EXISTS (SELECT 1 FROM "McpGrants" WHERE "AccountSessionId" = NEW."AccountSessionId")
                        BEGIN
                            SELECT RAISE(ABORT, 'MCP families cannot hold browser credentials');
                        END;
                        """);
                }
            }

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite") return;
            foreach (var table in new[] { "OidcProviderSessions", "RefreshTokenSessions" })
            {
                foreach (var operation in new[] { "INSERT", "UPDATE" })
                {
                    migrationBuilder.Sql($"DROP TRIGGER \"TR_{table}_NoMcpFamily_{operation}\";");
                }
            }

            migrationBuilder.DropTable(
                name: "McpGrants");

            migrationBuilder.DropTable(
                name: "OpenIddictScopes");

            migrationBuilder.DropTable(
                name: "OpenIddictTokens");

            migrationBuilder.DropTable(
                name: "OpenIddictAuthorizations");

            migrationBuilder.DropTable(
                name: "OpenIddictApplications");
            migrationBuilder.DropIndex(
                name: "IX_OidcProviderSessions_Id_TenantId_UserAccountId",
                table: "OidcProviderSessions");
        }
    }
}
