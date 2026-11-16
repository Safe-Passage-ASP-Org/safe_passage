using Microsoft.EntityFrameworkCore.Migrations;

namespace SafePassage.Api.Data.Migrations;

public partial class AddApplicationLaunchUrl : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LaunchUrl",
            table: "Applications",
            type: "TEXT",
            maxLength: 2048,
            nullable: false,
            defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "LaunchUrl",
            table: "Applications");
    }
}
