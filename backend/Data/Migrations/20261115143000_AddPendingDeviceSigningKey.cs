using Microsoft.EntityFrameworkCore.Migrations;

namespace SafePassage.Api.Data.Migrations;

public partial class AddPendingDeviceSigningKey : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PendingSigningPublicKey",
            table: "Devices",
            type: "TEXT",
            maxLength: 128,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PendingSigningPublicKey",
            table: "Devices");
    }
}
