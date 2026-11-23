using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261123093000_AddBackupEmailVerification")]
public partial class AddBackupEmailVerification : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "BackupEmail", table: "Users", maxLength: 320, nullable: true);
        migrationBuilder.CreateIndex(
            name: "IX_Users_BackupEmail", table: "Users", column: "BackupEmail",
            unique: true, filter: "\"BackupEmail\" IS NOT NULL");
        migrationBuilder.CreateTable(
            name: "BackupEmailVerificationTokens",
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
                table.PrimaryKey("PK_BackupEmailVerificationTokens", x => x.Id);
                table.ForeignKey("FK_BackupEmailVerificationTokens_Users_UserId", x => x.UserId,
                    "Users", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("IX_BackupEmailVerificationTokens_TokenHash",
            "BackupEmailVerificationTokens", "TokenHash", unique: true);
        migrationBuilder.CreateIndex("IX_BackupEmailVerificationTokens_UserId_ExpiresAt",
            "BackupEmailVerificationTokens", new[] { "UserId", "ExpiresAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "BackupEmailVerificationTokens");
        migrationBuilder.DropIndex(name: "IX_Users_BackupEmail", table: "Users");
        migrationBuilder.DropColumn(name: "BackupEmail", table: "Users");
    }
}
