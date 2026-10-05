using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SafePassage.Api.Controllers;

[Authorize(Roles = "Admin,Administrator,Super Admin")]
[ApiController]
[Route("api/admin/roles")]
public sealed class RolesController : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll() => Ok(new[]
    {
        new { id = "admin", name = "Administrator", description = "Full control over users, devices, and policies." },
        new { id = "auditor", name = "Auditor", description = "Read-only access for reporting and alert review." },
        new { id = "operator", name = "Operator", description = "Can manage users, devices, and app assignments within assigned scopes." },
        new { id = "user", name = "User", description = "Standard end-user access limited to assigned applications." }
    });
}
