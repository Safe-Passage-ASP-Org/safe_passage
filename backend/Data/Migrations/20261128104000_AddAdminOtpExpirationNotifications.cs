using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261128104000_AddAdminOtpExpirationNotifications")]
public partial class AddAdminOtpExpirationNotifications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ExpirationWarningSentAt",
            table: "AdminLoginTokens",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ExpiredNoticeSentAt",
            table: "AdminLoginTokens",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ExpirationWarningSentAt",
            table: "AdminLoginTokens");

        migrationBuilder.DropColumn(
            name: "ExpiredNoticeSentAt",
            table: "AdminLoginTokens");
    }
}
