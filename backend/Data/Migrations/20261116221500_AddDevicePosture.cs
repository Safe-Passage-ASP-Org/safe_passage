using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261116221500_AddDevicePosture")]
public partial class AddDevicePosture : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsRootedOrJailbroken",
            table: "Devices",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PostureUpdatedAt",
            table: "Devices",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "ScreenLockEnabled",
            table: "Devices",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "IsRootedOrJailbroken",
            table: "Devices");

        migrationBuilder.DropColumn(
            name: "PostureUpdatedAt",
            table: "Devices");

        migrationBuilder.DropColumn(
            name: "ScreenLockEnabled",
            table: "Devices");
    }
}
