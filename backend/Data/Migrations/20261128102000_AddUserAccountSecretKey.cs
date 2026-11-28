using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261128102000_AddUserAccountSecretKey")]
public partial class AddUserAccountSecretKey : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AccountSecretKeyHash",
            table: "Users",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AccountSecretKeyPrefix",
            table: "Users",
            maxLength: 24,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "AccountSecretKeyCreatedAt",
            table: "Users",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AccountSecretKeyHash",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "AccountSecretKeyPrefix",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "AccountSecretKeyCreatedAt",
            table: "Users");
    }
}
