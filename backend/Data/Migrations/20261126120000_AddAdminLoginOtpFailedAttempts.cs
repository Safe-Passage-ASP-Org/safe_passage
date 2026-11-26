using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261126120000_AddAdminLoginOtpFailedAttempts")]
public partial class AddAdminLoginOtpFailedAttempts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<int>(
            name: "FailedAttempts",
            table: "AdminLoginTokens",
            type: "integer",
            nullable: false,
            defaultValue: 0);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "FailedAttempts", table: "AdminLoginTokens");
}
