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
        options.ConfigureSafePassageDatabase(provider, connectionString, contentRoot);

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
}