using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261116123000_AddUserSubscriptions")]
public partial class AddUserSubscriptions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserSubscriptions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                PayPalSubscriptionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                PayPalPlanId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                PlanCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                ApprovalUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                CurrentPeriodStart = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CurrentPeriodEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CancelledAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserSubscriptions", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserSubscriptions_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "SubscriptionWebhookEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PayPalEventId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                EventType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                PayPalSubscriptionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                ProcessedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                PayloadSummary = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SubscriptionWebhookEvents", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserSubscriptions_PayPalSubscriptionId",
            table: "UserSubscriptions",
            column: "PayPalSubscriptionId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserSubscriptions_UserId_Status",
            table: "UserSubscriptions",
            columns: new[] { "UserId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_SubscriptionWebhookEvents_PayPalEventId",
            table: "SubscriptionWebhookEvents",
            column: "PayPalEventId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SubscriptionWebhookEvents");
        migrationBuilder.DropTable(name: "UserSubscriptions");
    }
}
