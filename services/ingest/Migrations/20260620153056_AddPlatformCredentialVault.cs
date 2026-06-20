using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameHub.Ingest.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformCredentialVault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var isPostgres = migrationBuilder.ActiveProvider ==
                "Npgsql.EntityFrameworkCore.PostgreSQL";
            var integerType = isPostgres ? "integer" : "INTEGER";
            var tenantIdType = isPostgres ? "uuid" : "TEXT";
            var binaryType = isPostgres ? "bytea" : "BLOB";
            var timestampType = isPostgres ? "timestamp with time zone" : "TEXT";

            migrationBuilder.CreateTable(
                name: "PlatformCredentials",
                columns: table => new
                {
                    Id = isPostgres
                        ? table.Column<int>(type: integerType, nullable: false)
                            .Annotation(
                                "Npgsql:ValueGenerationStrategy",
                                NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                        : table.Column<int>(type: integerType, nullable: false)
                            .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<Guid>(
                        type: tenantIdType,
                        nullable: false,
                        defaultValue: new Guid("00000000-0000-0000-0000-000000000001")),
                    Platform = table.Column<int>(type: integerType, nullable: false),
                    CredentialSchemaVersion = table.Column<int>(type: integerType, nullable: false),
                    EncryptionFormatVersion = table.Column<int>(type: integerType, nullable: false),
                    EncryptionKeyVersion = table.Column<int>(type: integerType, nullable: false),
                    EncryptedPayload = table.Column<byte[]>(
                        type: binaryType,
                        maxLength: 16384,
                        nullable: false),
                    Nonce = table.Column<byte[]>(type: binaryType, maxLength: 12, nullable: false),
                    AuthenticationTag = table.Column<byte[]>(
                        type: binaryType,
                        maxLength: 16,
                        nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: timestampType, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: timestampType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformCredentials", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformCredentials_TenantId_Platform",
                table: "PlatformCredentials",
                columns: new[] { "TenantId", "Platform" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformCredentials");
        }
    }
}
