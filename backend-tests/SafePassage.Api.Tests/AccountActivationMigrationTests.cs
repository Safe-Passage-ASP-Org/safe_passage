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
    }
}
