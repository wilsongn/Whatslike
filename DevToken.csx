#r "nuget: System.IdentityModel.Tokens.Jwt, 7.6.0"
#r "nuget: Microsoft.IdentityModel.Tokens, 7.6.0"

using System;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;

var issuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "chat-dev";
var audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "chat-api";
var secret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "26c8d9a793975af4999bc048990f6fd1";
var user = Environment.GetEnvironmentVariable("JWT_USER") ?? "wilson";
var minutes = int.TryParse(Environment.GetEnvironmentVariable("JWT_MINUTES"), out var m) ? m : 120;

var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
var now = DateTime.UtcNow;

var jwt = new JwtSecurityToken(
    issuer: issuer,
    audience: audience,
    claims: new[]
    {
        new Claim(JwtRegisteredClaimNames.Sub, user),
        new Claim("name", user),
    },
    notBefore: now,
    expires: now.AddMinutes(minutes),
    signingCredentials: creds
);

var token = new JwtSecurityTokenHandler().WriteToken(jwt);
Console.WriteLine(token);
