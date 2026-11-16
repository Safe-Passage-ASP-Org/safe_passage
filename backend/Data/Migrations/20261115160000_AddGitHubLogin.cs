using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261115160000_AddGitHubLogin")]
public partial class AddGitHubLogin : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "GitHubUserId",
            table: "Users",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Users_GitHubUserId",
            table: "Users",
            column: "GitHubUserId",
            unique: true,
            filter: "\"GitHubUserId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Users_GitHubUserId",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "GitHubUserId",
            table: "Users");
    }
}
