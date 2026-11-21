using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261121090000_AddAdminEmailChangeTokens")]
public partial class AddAdminEmailChangeTokens : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AdminEmailChangeTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                UserId = table.Column<Guid>(nullable: false),
                NewEmail = table.Column<string>(maxLength: 320, nullable: false),
                TokenHash = table.Column<string>(maxLength: 64, nullable: false),
                OtpHash = table.Column<string>(maxLength: 64, nullable: false),
                FailedAttempts = table.Column<int>(nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(nullable: false),
                UsedAt = table.Column<DateTimeOffset>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AdminEmailChangeTokens", x => x.Id);
                table.ForeignKey("FK_AdminEmailChangeTokens_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("IX_AdminEmailChangeTokens_TokenHash", "AdminEmailChangeTokens", "TokenHash", unique: true);
        migrationBuilder.CreateIndex("IX_AdminEmailChangeTokens_UserId_ExpiresAt", "AdminEmailChangeTokens", new[] { "UserId", "ExpiresAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "AdminEmailChangeTokens");
}
