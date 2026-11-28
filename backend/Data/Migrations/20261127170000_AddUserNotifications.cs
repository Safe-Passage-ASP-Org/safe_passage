using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261127170000_AddUserNotifications")]
public partial class AddUserNotifications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserNotifications",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                UserId = table.Column<Guid>(nullable: false),
                Category = table.Column<string>(maxLength: 64, nullable: false),
                Title = table.Column<string>(maxLength: 120, nullable: false),
                Message = table.Column<string>(maxLength: 2000, nullable: false),
                Href = table.Column<string>(maxLength: 512, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                ReadAt = table.Column<DateTimeOffset>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserNotifications", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserNotifications_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserNotifications_UserId_CreatedAt",
            table: "UserNotifications",
            columns: new[] { "UserId", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "UserNotifications");
    }
}
