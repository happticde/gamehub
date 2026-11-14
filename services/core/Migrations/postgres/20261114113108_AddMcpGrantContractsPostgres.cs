using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using GameHub.Core.Data;

#nullable disable

namespace GameHub.Core.Migrations.postgres
{
    /// <inheritdoc />
    [DbContext(typeof(CoreContext))]
    [Migration("20261114113108_AddMcpGrantContractsPostgres")]
    public partial class AddMcpGrantContractsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

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
                    Id = table.Column<string>(type: "text", nullable: false),
                    ApplicationType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ClientSecret = table.Column<string>(type: "text", nullable: true),
                    ClientType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ConsentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    DisplayNames = table.Column<string>(type: "text", nullable: true),
                    JsonWebKeySet = table.Column<string>(type: "text", nullable: true),
                    Permissions = table.Column<string>(type: "text", nullable: true),
                    PostLogoutRedirectUris = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    RedirectUris = table.Column<string>(type: "text", nullable: true),
                    Requirements = table.Column<string>(type: "text", nullable: true),
                    Settings = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictScopes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Descriptions = table.Column<string>(type: "text", nullable: true),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    DisplayNames = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    Resources = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictScopes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictAuthorizations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    Scopes = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
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
                    Id = table.Column<string>(type: "text", nullable: false),
                    ApplicationId = table.Column<string>(type: "text", nullable: true),
                    AuthorizationId = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpirationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Payload = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    RedemptionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReferenceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProtocolAuthorizationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OidcProviderSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Resource = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Scopes = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApprovedSessionVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                CREATE FUNCTION "GameHub_McpGrantGuard"() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        IF (to_jsonb(OLD) - 'RevokedAtUtc') IS DISTINCT FROM (to_jsonb(NEW) - 'RevokedAtUtc')
                           OR (OLD."RevokedAtUtc" IS NOT NULL AND OLD."RevokedAtUtc" IS DISTINCT FROM NEW."RevokedAtUtc") THEN
                            RAISE EXCEPTION 'MCP consent is immutable';
                        END IF;
                    ELSE
                        IF EXISTS (SELECT 1 FROM "OidcProviderSessions" WHERE "AccountSessionId" = NEW."AccountSessionId")
                           OR EXISTS (SELECT 1 FROM "RefreshTokenSessions" WHERE "AccountSessionId" = NEW."AccountSessionId") THEN
                            RAISE EXCEPTION 'MCP requires an independent family';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER "TR_McpGrants_Guard" BEFORE INSERT OR UPDATE ON "McpGrants"
                FOR EACH ROW EXECUTE FUNCTION "GameHub_McpGrantGuard"();
                CREATE FUNCTION "GameHub_NoMcpBrowserFamily"() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "McpGrants" WHERE "AccountSessionId" = NEW."AccountSessionId") THEN
                        RAISE EXCEPTION 'MCP families cannot hold browser credentials';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER "TR_OidcProviderSessions_NoMcpFamily" BEFORE INSERT OR UPDATE ON "OidcProviderSessions"
                FOR EACH ROW EXECUTE FUNCTION "GameHub_NoMcpBrowserFamily"();
                CREATE TRIGGER "TR_RefreshTokenSessions_NoMcpFamily" BEFORE INSERT OR UPDATE ON "RefreshTokenSessions"
                FOR EACH ROW EXECUTE FUNCTION "GameHub_NoMcpBrowserFamily"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;
            migrationBuilder.Sql("""
                DROP TRIGGER "TR_OidcProviderSessions_NoMcpFamily" ON "OidcProviderSessions";
                DROP TRIGGER "TR_RefreshTokenSessions_NoMcpFamily" ON "RefreshTokenSessions";
                DROP TRIGGER "TR_McpGrants_Guard" ON "McpGrants";
                DROP FUNCTION "GameHub_NoMcpBrowserFamily"();
                DROP FUNCTION "GameHub_McpGrantGuard"();
                """);

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
