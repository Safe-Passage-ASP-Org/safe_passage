using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261115220000_AddGitLabUserId")]
public partial class AddGitLabUserId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "GitLabUserId",
            table: "Users",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Users_GitLabUserId",
            table: "Users",
            column: "GitLabUserId",
            unique: true,
            filter: "\"GitLabUserId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Users_GitLabUserId",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "GitLabUserId",
            table: "Users");
    }
}
