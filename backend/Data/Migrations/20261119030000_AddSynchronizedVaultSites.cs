using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261119030000_AddSynchronizedVaultSites")]
public partial class AddSynchronizedVaultSites : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "VaultSites",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                UserId = table.Column<Guid>(nullable: false),
                Name = table.Column<string>(maxLength: 200, nullable: false),
                LoginUrl = table.Column<string>(maxLength: 2048, nullable: false),
                Origin = table.Column<string>(maxLength: 512, nullable: false),
                Category = table.Column<string>(maxLength: 64, nullable: false),
                Description = table.Column<string>(maxLength: 2000, nullable: false),
                AutoFillUsername = table.Column<string>(maxLength: 320, nullable: false),
                EncryptedPassword = table.Column<string>(maxLength: 2048, nullable: false),
                EncryptedTotpSecret = table.Column<string>(maxLength: 2048, nullable: false),
                VerificationStatus = table.Column<string>(maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                VerifiedAt = table.Column<DateTimeOffset>(nullable: true),
                VerifiedByUserId = table.Column<Guid>(nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VaultSites", x => x.Id);
                table.ForeignKey("FK_VaultSites_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(name: "IX_VaultSites_UserId_Origin", table: "VaultSites", columns: new[] { "UserId", "Origin" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_VaultSites_VerificationStatus_CreatedAt", table: "VaultSites", columns: new[] { "VerificationStatus", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "VaultSites");
}
