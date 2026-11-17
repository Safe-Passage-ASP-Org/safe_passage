using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261117120000_AddDevicePushToken")]
public partial class AddDevicePushToken : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PushPlatform",
            table: "Devices",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PushToken",
            table: "Devices",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PushPlatform",
            table: "Devices");

        migrationBuilder.DropColumn(
            name: "PushToken",
            table: "Devices");
    }
}
