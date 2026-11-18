using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SafePassage.Api.Data;

#nullable disable

namespace SafePassage.Api.Data.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261118200000_AddApplicationCategoriesAndAutofill")]
public partial class AddApplicationCategoriesAndAutofill : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Category",
            table: "Applications",
            maxLength: 64,
            nullable: false,
            defaultValue: "Other");
        migrationBuilder.AddColumn<string>(
            name: "AutoFillUsername",
            table: "Applications",
            maxLength: 320,
            nullable: false,
            defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "EncryptedAutoFillPassword",
            table: "Applications",
            maxLength: 2048,
            nullable: false,
            defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "Purpose",
            table: "MfaSessions",
            maxLength: 64,
            nullable: false,
            defaultValue: "Login");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Category", table: "Applications");
        migrationBuilder.DropColumn(name: "AutoFillUsername", table: "Applications");
        migrationBuilder.DropColumn(name: "EncryptedAutoFillPassword", table: "Applications");
        migrationBuilder.DropColumn(name: "Purpose", table: "MfaSessions");
    }
}
