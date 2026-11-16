using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SafePassage.Api.Data;

public static class DbContextOptionsExtensions
{
    public static void ConfigureSafePassageDatabase(
        this DbContextOptionsBuilder options,
        string provider,
        string connectionString,
        string contentRootPath)
    {
        switch (provider.Trim().ToLowerInvariant())
        {
            case "sqlserver":
                options.UseSqlServer(connectionString);
                break;
            case "postgresql":
            case "postgres":
                options.UseNpgsql(connectionString);
                break;
            case "sqlite":
                options.UseSqlite(ResolveSqliteConnectionString(connectionString, contentRootPath));
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported Database:Provider '{provider}'. Use 'SqlServer', 'PostgreSQL', or 'Sqlite'.");
        }
    }

    // SQLite resolves a relative Data Source against the process working directory, which differs
    // depending on how the API is launched. Anchor it to the content root so every launch uses the same file.
    private static string ResolveSqliteConnectionString(string connectionString, string contentRootPath)
    {
        var sqliteBuilder = new SqliteConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(sqliteBuilder.DataSource) && !Path.IsPathRooted(sqliteBuilder.DataSource))
            sqliteBuilder.DataSource = Path.GetFullPath(Path.Combine(contentRootPath, sqliteBuilder.DataSource));

        var databaseDirectory = Path.GetDirectoryName(sqliteBuilder.DataSource);
        if (!string.IsNullOrEmpty(databaseDirectory))
            Directory.CreateDirectory(databaseDirectory);

        return sqliteBuilder.ConnectionString;
    }
}
