using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Data;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class AccountActivationMigrationTests
{
    [Fact]
    public async Task Migrate_CreatesActivationStorageAndKeepsExistingAccountsVerified()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var dbContext = new ApplicationDbContext(options);
        await dbContext.Database.MigrateAsync();

        var dbConnection = dbContext.Database.GetDbConnection();
        await using var command = dbConnection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"Users\")";
        string? emailVerifiedDefault = null;
        var userColumns = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(1);
                userColumns.Add(name);
                if (name == "IsEmailVerified")
                    emailVerifiedDefault = reader.IsDBNull(4) ? null : reader.GetString(4);
            }
        }

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'AccountActivationTokens'";
        var activationTokenTableExists = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal("1", emailVerifiedDefault);
        Assert.Equal(1, activationTokenTableExists);
        Assert.Contains("FullName", userColumns);
        Assert.Contains("PhoneNumber", userColumns);

        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Devices'";
        var deviceIndexes = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                deviceIndexes.Add(reader.GetString(0));
        }
        Assert.Contains("IX_Devices_UserId_DeviceId", deviceIndexes);
        Assert.DoesNotContain("IX_Devices_DeviceId", deviceIndexes);

        var firstUser = new SafePassage.Api.Models.User
        {
            Id = Guid.NewGuid(),
            Username = "first",
            Email = "first@example.com"
        };
        var secondUser = new SafePassage.Api.Models.User
        {
            Id = Guid.NewGuid(),
            Username = "second",
            Email = "second@example.com"
        };
        dbContext.Devices.AddRange(
            new SafePassage.Api.Models.Device
            {
                Id = Guid.NewGuid(),
                UserId = firstUser.Id,
                DeviceId = "shared-phone",
                DeviceName = "Phone",
                DeviceType = "Phone",
                OS = "Android"
            },
            new SafePassage.Api.Models.Device
            {
                Id = Guid.NewGuid(),
                UserId = secondUser.Id,
                DeviceId = "shared-phone",
                DeviceName = "Phone",
                DeviceType = "Phone",
                OS = "Android"
            });
        dbContext.Users.AddRange(firstUser, secondUser);
        await dbContext.SaveChangesAsync();
    }
}
