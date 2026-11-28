using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SafePassage.Api.Controllers;
using SafePassage.Api.Data;
using SafePassage.Api.Models;
using Xunit;

namespace SafePassage.Api.Tests;

public sealed class GroupsControllerTests
{
    [Fact]
    public async Task Update_ReplacesGroupMembers()
    {
        await using var dbContext = CreateDbContext();
        var existingMember = new User
        {
            Id = Guid.NewGuid(),
            Username = "existing",
            Email = "existing@example.com"
        };
        var newMember = new User
        {
            Id = Guid.NewGuid(),
            Username = "new",
            Email = "new@example.com"
        };
        var group = new Group { Id = Guid.NewGuid(), Name = "Engineering" };
        dbContext.AddRange(existingMember, newMember, group);
        dbContext.UserGroups.Add(new UserGroup { UserId = existingMember.Id, GroupId = group.Id });
        await dbContext.SaveChangesAsync();
        var controller = new GroupsController(dbContext);

        var response = await controller.Update(
            group.Id,
            new GroupsController.GroupRequest("Product", "Product team", [newMember.Id]),
            CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        var summary = Assert.IsType<GroupsController.GroupSummary>(result.Value);
        Assert.Equal(new[] { newMember.Id }, summary.MemberUserIds);
        Assert.False(await dbContext.UserGroups.AnyAsync(
            membership => membership.GroupId == group.Id && membership.UserId == existingMember.Id));
        Assert.True(await dbContext.UserGroups.AnyAsync(
            membership => membership.GroupId == group.Id && membership.UserId == newMember.Id));
    }

    private static ApplicationDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
