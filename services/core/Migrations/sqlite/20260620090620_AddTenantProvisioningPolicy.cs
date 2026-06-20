using GameHub.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameHub.Core.Migrations.sqlite;

/// <inheritdoc />
[DbContext(typeof(CoreContext))]
[Migration("20260620090620_AddTenantProvisioningPolicy")]
public partial class AddTenantProvisioningPolicy : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            return;
        }

        // False is the safe default for every newly provisioned tenant. The update preserves
        // compatibility only for the legacy tenant that already owns the shared Ingest data.
        migrationBuilder.AddColumn<bool>(
            name: "PlatformSyncEnabled",
            table: "Tenants",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        // This provider-specific migration intentionally has no generated Designer model.
        // Raw SQL avoids UpdateData's model-mapping requirement while still targeting only
        // the stable, server-defined compatibility tenant.
        migrationBuilder.Sql(
            "UPDATE \"Tenants\" SET \"PlatformSyncEnabled\" = 1 " +
            $"WHERE \"Id\" = '{TenantDefaults.DefaultTenantId:D}'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            return;
        }

        migrationBuilder.DropColumn(
            name: "PlatformSyncEnabled",
            table: "Tenants");
    }
}
