using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SafePassage.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceSigningKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SigningPublicKey",
                table: "Devices",
                type: "TEXT",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SigningPublicKey",
                table: "Devices");
        }
    }
}
