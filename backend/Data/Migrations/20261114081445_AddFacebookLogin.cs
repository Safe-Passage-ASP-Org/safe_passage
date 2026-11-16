using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SafePassage.Api.Data.Migrations
{
    public partial class AddFacebookLogin : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FacebookUserId",
                table: "Users",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_FacebookUserId",
                table: "Users",
                column: "FacebookUserId",
                unique: true,
                filter: "\"FacebookUserId\" IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_FacebookUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FacebookUserId",
                table: "Users");
        }
    }
}
