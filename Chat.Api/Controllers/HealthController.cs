using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chat.Api.Controllers;

[ApiController]
[Route("v1/healthz")]
public class HealthController : ControllerBase
{
    [Authorize]
    [HttpGet("me")]
    public IActionResult Me()
    {
        return Ok(new
        {
            name = User.Identity?.Name,
            sub = User.FindFirstValue(ClaimTypes.NameIdentifier),
            tenant = User.FindFirst("tenant_id")?.Value
        });
    }
}
