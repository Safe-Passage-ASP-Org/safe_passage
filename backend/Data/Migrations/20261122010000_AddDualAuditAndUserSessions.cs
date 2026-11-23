using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261122010000_AddDualAuditAndUserSessions")]
public partial class AddDualAuditAndUserSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ActorUserId",
            table: "ActivityLogs",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "TargetUserId",
            table: "ActivityLogs",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DeviceId",
            table: "ActivityLogs",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Reason",
            table: "ActivityLogs",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "IpAddress",
            table: "ActivityLogs",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ActivityLogs_TargetUserId_Timestamp",
            table: "ActivityLogs",
            columns: new[] { "TargetUserId", "Timestamp" });

        migrationBuilder.CreateTable(
            name: "UserSessions",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                UserId = table.Column<Guid>(nullable: false),
                DeviceId = table.Column<string>(maxLength: 256, nullable: true),
                TokenJti = table.Column<string>(maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(nullable: false),
                RevokedAt = table.Column<DateTimeOffset>(nullable: true),
                RevokedReason = table.Column<string>(maxLength: 128, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserSessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserSessions_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserSessions_TokenJti",
            table: "UserSessions",
            column: "TokenJti",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserSessions_UserId_DeviceId_RevokedAt",
            table: "UserSessions",
            columns: new[] { "UserId", "DeviceId", "RevokedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "UserSessions");
        migrationBuilder.DropIndex(name: "IX_ActivityLogs_TargetUserId_Timestamp", table: "ActivityLogs");
        migrationBuilder.DropColumn(name: "ActorUserId", table: "ActivityLogs");
        migrationBuilder.DropColumn(name: "TargetUserId", table: "ActivityLogs");
        migrationBuilder.DropColumn(name: "DeviceId", table: "ActivityLogs");
        migrationBuilder.DropColumn(name: "Reason", table: "ActivityLogs");
        migrationBuilder.DropColumn(name: "IpAddress", table: "ActivityLogs");
    }
}
