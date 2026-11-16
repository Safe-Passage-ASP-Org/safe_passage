using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SafePassage.Api.Controllers;
using SafePassage.Api.Data;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class HealthControllerTests
{
    [Fact]
    public void GetLive_ReturnsHealthyLivenessResponse()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var dbContext = CreateDbContext(connection);
        var controller = new HealthController(dbContext, NullLogger<HealthController>.Instance);

        var result = controller.GetLive();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<HealthCheckResponse>(ok.Value);
        Assert.Equal("Healthy", payload.Status);
        Assert.Equal("liveness", payload.Check);
        Assert.Null(payload.Dependencies);
    }

    [Fact]
    public async Task GetReady_ReturnsReadyWhenDatabaseIsReachable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var dbContext = CreateDbContext(connection);
        await dbContext.Database.EnsureCreatedAsync();
        var controller = new HealthController(dbContext, NullLogger<HealthController>.Instance);

        var result = await controller.GetReady(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<HealthCheckResponse>(ok.Value);
        Assert.Equal("Ready", payload.Status);
        Assert.Equal("readiness", payload.Check);
        Assert.Equal("Healthy", payload.Dependencies?.Database);
    }

    [Fact]
    public async Task GetReady_Returns503WithoutLeakingSensitiveDetailsWhenDatabaseIsUnavailable()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nonexistent;Username=secretuser;Password=secretpass")
            .Options;
        await using var dbContext = new ApplicationDbContext(options);
        var controller = new HealthController(dbContext, NullLogger<HealthController>.Instance);

        var result = await controller.GetReady(CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusResult.StatusCode);
        var payload = Assert.IsType<HealthCheckResponse>(statusResult.Value);
        Assert.Equal("Unhealthy", payload.Status);
        Assert.Equal("readiness", payload.Check);
        Assert.Equal("Unhealthy", payload.Dependencies?.Database);
    }

    private static ApplicationDbContext CreateDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        return new ApplicationDbContext(options);
    }
}
