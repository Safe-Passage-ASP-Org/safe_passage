using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261104141500_AllowSharedDeviceIdsAcrossAccounts")]
public sealed class AllowSharedDeviceIdsAcrossAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Devices_DeviceId",
            table: "Devices");

        migrationBuilder.CreateIndex(
            name: "IX_Devices_UserId_DeviceId",
            table: "Devices",
            columns: new[] { "UserId", "DeviceId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Devices_UserId_DeviceId",
            table: "Devices");

        migrationBuilder.CreateIndex(
            name: "IX_Devices_DeviceId",
            table: "Devices",
            column: "DeviceId",
            unique: true);
    }
}
