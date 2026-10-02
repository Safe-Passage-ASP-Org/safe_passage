using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SafePassage.Api.Data;

namespace SafePassage.Api.Data;

public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var contentRoot = ResolveContentRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRoot
        });
        var provider = builder.Configuration["Database:Provider"] ?? "SqlServer";
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be configured.");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();

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
                options.UseSqlite(ResolveSqliteConnectionString(connectionString, contentRoot));
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported Database:Provider '{provider}'. Use 'SqlServer', 'PostgreSQL', or 'Sqlite'.");
        }

        return new ApplicationDbContext(options.Options);
    }

    private static string ResolveContentRoot()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(currentDirectory, "SafePassage.Api.csproj")))
            return currentDirectory;

        var backendDirectory = Path.Combine(currentDirectory, "backend");
        return File.Exists(Path.Combine(backendDirectory, "SafePassage.Api.csproj"))
            ? backendDirectory
            : currentDirectory;
    }

    private static string ResolveSqliteConnectionString(string connectionString, string contentRoot)
    {
        var sqliteBuilder = new SqliteConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(sqliteBuilder.DataSource) && !Path.IsPathRooted(sqliteBuilder.DataSource))
            sqliteBuilder.DataSource = Path.GetFullPath(Path.Combine(contentRoot, sqliteBuilder.DataSource));

        var databaseDirectory = Path.GetDirectoryName(sqliteBuilder.DataSource);
        if (!string.IsNullOrEmpty(databaseDirectory))
            Directory.CreateDirectory(databaseDirectory);

        return sqliteBuilder.ConnectionString;
    }
}