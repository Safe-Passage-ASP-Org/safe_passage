using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SafePassage.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActivationPasswordSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresPasswordSetup",
                table: "AccountActivationTokens",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequiresPasswordSetup",
                table: "AccountActivationTokens");
        }
    }
}
