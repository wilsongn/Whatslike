using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace Chat.Api.Controllers;

[ApiController]
[Route("v1/auth")]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _cfg;
    public AuthController(IConfiguration cfg) => _cfg = cfg;

    public record LoginRequest(Guid OrganizacaoId, Guid UsuarioId, string? Nome, string Password);

    [AllowAnonymous]
    [HttpPost("token")]
    public IActionResult Token([FromBody] LoginRequest req)
    {
        // DEV-ONLY: aceite básico; substitua por validação real depois
        if (string.IsNullOrWhiteSpace(req.Password)) return Unauthorized();

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, req.UsuarioId.ToString()),
            new Claim(ClaimTypes.Name, req.Nome ?? "user"),
            new Claim("tenant_id", req.OrganizacaoId.ToString())
        };

        var issuer = _cfg["Jwt:Issuer"];
        var audience = _cfg["Jwt:Audience"];
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_cfg["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var disableLifetime = _cfg.GetValue<bool>("Jwt:DisableLifetimeValidation");

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: disableLifetime ? (DateTime?)null : DateTime.UtcNow.AddHours(4), // sem exp em dev se flag=true
            signingCredentials: creds
        );


        return Ok(new
        {
            access_token = new JwtSecurityTokenHandler().WriteToken(token),
            token_type = "Bearer",
            expires_in = 4 * 3600
        });
    }
}
