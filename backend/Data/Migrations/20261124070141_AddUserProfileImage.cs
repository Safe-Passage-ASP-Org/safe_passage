using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261124070141_AddUserProfileImage")]
public partial class AddUserProfileImage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProfileImageData",
            table: "Users",
            maxLength: 1400000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ProfileImageData",
            table: "Users");
    }
}
