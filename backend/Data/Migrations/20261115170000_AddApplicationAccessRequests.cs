using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261115170000_AddApplicationAccessRequests")]
public partial class AddApplicationAccessRequests : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsPublished",
            table: "Applications",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "AllowAccessRequests",
            table: "Applications",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "ApplicationAccessRequests",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                RequestedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ReviewedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                ReviewedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                DecisionNote = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ApplicationAccessRequests", x => x.Id);
                table.ForeignKey(
                    name: "FK_ApplicationAccessRequests_Applications_ApplicationId",
                    column: x => x.ApplicationId,
                    principalTable: "Applications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ApplicationAccessRequests_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ApplicationAccessRequests_ApplicationId_UserId",
            table: "ApplicationAccessRequests",
            columns: new[] { "ApplicationId", "UserId" },
            unique: true,
            filter: "\"Status\" = 'Pending'");

        migrationBuilder.CreateIndex(
            name: "IX_ApplicationAccessRequests_UserId_RequestedAt",
            table: "ApplicationAccessRequests",
            columns: new[] { "UserId", "RequestedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ApplicationAccessRequests");
        migrationBuilder.DropColumn(name: "IsPublished", table: "Applications");
        migrationBuilder.DropColumn(name: "AllowAccessRequests", table: "Applications");
    }
}
