using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Controllers;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class UserApplicationsControllerTests
{
    [Fact]
    public async Task GetMine_ReturnsDirectAndGroupAssignmentsOnly()
    {
        await using var dbContext = CreateDbContext();
        var user = new User { Id = Guid.NewGuid(), Username = "alice", Email = "alice@example.com" };
        var otherUser = new User { Id = Guid.NewGuid(), Username = "bob", Email = "bob@example.com" };
        var group = new Group { Id = Guid.NewGuid(), Name = "Finance" };
        var directApp = new Application { Id = Guid.NewGuid(), Name = "Direct app" };
        var groupApp = new Application { Id = Guid.NewGuid(), Name = "Group app" };
        var unassignedApp = new Application { Id = Guid.NewGuid(), Name = "Unassigned app" };
        dbContext.AddRange(user, otherUser, group, directApp, groupApp, unassignedApp);
        dbContext.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = group.Id });
        dbContext.ApplicationUserAccesses.Add(new ApplicationUserAccess
        {
            UserId = user.Id,
            ApplicationId = directApp.Id
        });
        dbContext.ApplicationGroupAccesses.Add(new ApplicationGroupAccess
        {
            GroupId = group.Id,
            ApplicationId = groupApp.Id
        });
        dbContext.ApplicationUserAccesses.Add(new ApplicationUserAccess
        {
            UserId = otherUser.Id,
            ApplicationId = unassignedApp.Id
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, user.Id);
        var response = await controller.GetMine(CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        var applications = Assert.IsAssignableFrom<IReadOnlyList<UserApplicationsController.AccessibleApplication>>(result.Value);
        Assert.Equal(new[] { "Direct app", "Group app" }, applications.Select(application => application.Name));
    }

    [Fact]
    public async Task GetMine_ReturnsUnauthorizedWithoutUserIdClaim()
    {
        await using var dbContext = CreateDbContext();
        var controller = new UserApplicationsController(dbContext)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
            }
        };

        var response = await controller.GetMine(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(response.Result);
    }

    private static UserApplicationsController CreateController(ApplicationDbContext dbContext, Guid userId) =>
        new(dbContext)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                        "test"))
                }
            }
        };

    private static ApplicationDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
