// Chat.Api/Controllers/UsersController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Chat.Api.Models;
using Chat.Api.Repositories;
using System.Security.Claims;

namespace Chat.Api.Controllers;

[ApiController]
[Route("api/v1/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly IUserRepository _userRepository;
    private readonly ILogger<UsersController> _logger;

    public UsersController(
        IUserRepository userRepository,
        ILogger<UsersController> logger)
    {
        _userRepository = userRepository;
        _logger = logger;
    }

    /// <summary>
    /// Obtém os dados do usuário logado
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser()
    {
        var userId = GetUserIdFromToken();
        var organizationId = GetOrganizationIdFromToken();

        if (userId == Guid.Empty)
        {
            return Unauthorized(new { error = "Invalid token" });
        }

        var user = await _userRepository.GetByIdAsync(userId);
        
        if (user == null)
        {
            // Se o usuário não existe no banco, criar com dados do JWT
            _logger.LogInformation("User not found, creating from JWT: {UserId}", userId);
            
            user = new User
            {
                UserId = userId,
                OrganizationId = organizationId,
                Username = $"user_{userId.ToString()[..8]}",
                DisplayName = $"Usuário {userId.ToString()[..8]}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            
            await _userRepository.UpsertAsync(user);
        }

        return Ok(new
        {
            userId = user.UserId,
            organizationId = user.OrganizationId,
            username = user.Username,
            displayName = user.DisplayName,
            email = user.Email,
            avatarUrl = user.AvatarUrl,
            createdAt = user.CreatedAt
        });
    }

    /// <summary>
    /// Obtém um usuário por ID
    /// </summary>
    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> GetUser(Guid userId)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        
        if (user == null)
        {
            return NotFound(new { error = "User not found" });
        }

        return Ok(new
        {
            userId = user.UserId,
            username = user.Username,
            displayName = user.DisplayName,
            avatarUrl = user.AvatarUrl
        });
    }

    /// <summary>
    /// Busca usuários por username ou nome
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> SearchUsers(
        [FromQuery] string q,
        [FromQuery] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
        {
            return BadRequest(new { error = "Query must be at least 2 characters" });
        }

        var organizationId = GetOrganizationIdFromToken();
        var currentUserId = GetUserIdFromToken();
        
        var users = await _userRepository.SearchAsync(organizationId, q, limit);
        
        // Remover o usuário atual dos resultados
        var filteredUsers = users.Where(u => u.UserId != currentUserId);

        return Ok(new
        {
            query = q,
            results = filteredUsers.Select(u => new
            {
                userId = u.UserId,
                username = u.Username,
                displayName = u.DisplayName,
                avatarUrl = u.AvatarUrl
            })
        });
    }

    /// <summary>
    /// Lista todos os usuários da organização
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListUsers([FromQuery] int limit = 50)
    {
        var organizationId = GetOrganizationIdFromToken();
        var currentUserId = GetUserIdFromToken();
        
        var users = await _userRepository.GetByOrganizationAsync(organizationId, limit);
        
        // Remover o usuário atual dos resultados
        var filteredUsers = users.Where(u => u.UserId != currentUserId);

        return Ok(new
        {
            organizationId,
            users = filteredUsers.Select(u => new
            {
                userId = u.UserId,
                username = u.Username,
                displayName = u.DisplayName,
                avatarUrl = u.AvatarUrl
            })
        });
    }

    /// <summary>
    /// Registra um novo usuário ou atualiza o existente
    /// </summary>
    [HttpPost("register")]
    public async Task<IActionResult> RegisterUser([FromBody] RegisterUserRequest request)
    {
        var userId = GetUserIdFromToken();
        var organizationId = GetOrganizationIdFromToken();

        if (userId == Guid.Empty)
        {
            return Unauthorized(new { error = "Invalid token" });
        }

        // Validar username
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length < 3)
        {
            return BadRequest(new { error = "Username must be at least 3 characters" });
        }

        // Validar caracteres do username (apenas letras, números, underscore)
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Username, @"^[a-zA-Z0-9_]+$"))
        {
            return BadRequest(new { error = "Username can only contain letters, numbers, and underscores" });
        }

        // Verificar se username já existe (de outro usuário)
        var existingUser = await _userRepository.GetByUsernameAsync(organizationId, request.Username);
        if (existingUser != null && existingUser.UserId != userId)
        {
            return Conflict(new { error = "Username already taken" });
        }

        var user = new User
        {
            UserId = userId,
            OrganizationId = organizationId,
            Username = request.Username.ToLowerInvariant().Trim(),
            DisplayName = request.DisplayName ?? request.Username,
            Email = request.Email?.ToLowerInvariant().Trim(),
            AvatarUrl = request.AvatarUrl
        };

        var savedUser = await _userRepository.UpsertAsync(user);

        _logger.LogInformation(
            "User registered: UserId={UserId}, Username={Username}",
            savedUser.UserId, savedUser.Username);

        return Ok(new
        {
            userId = savedUser.UserId,
            organizationId = savedUser.OrganizationId,
            username = savedUser.Username,
            displayName = savedUser.DisplayName,
            email = savedUser.Email,
            avatarUrl = savedUser.AvatarUrl,
            createdAt = savedUser.CreatedAt
        });
    }

    /// <summary>
    /// Atualiza o perfil do usuário
    /// </summary>
    [HttpPut("me")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = GetUserIdFromToken();
        var organizationId = GetOrganizationIdFromToken();

        var user = await _userRepository.GetByIdAsync(userId);
        if (user == null)
        {
            return NotFound(new { error = "User not found" });
        }

        // Atualizar campos permitidos
        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            user.DisplayName = request.DisplayName;
        }
        
        if (request.AvatarUrl != null)
        {
            user.AvatarUrl = request.AvatarUrl;
        }

        // Se tentar mudar username, verificar disponibilidade
        if (!string.IsNullOrWhiteSpace(request.Username) && 
            request.Username.ToLowerInvariant() != user.Username)
        {
            var existingUser = await _userRepository.GetByUsernameAsync(organizationId, request.Username);
            if (existingUser != null)
            {
                return Conflict(new { error = "Username already taken" });
            }
            user.Username = request.Username.ToLowerInvariant().Trim();
        }

        var updatedUser = await _userRepository.UpsertAsync(user);

        return Ok(new
        {
            userId = updatedUser.UserId,
            username = updatedUser.Username,
            displayName = updatedUser.DisplayName,
            avatarUrl = updatedUser.AvatarUrl
        });
    }

    /// <summary>
    /// Verifica se um username está disponível
    /// </summary>
    [HttpGet("check-username")]
    public async Task<IActionResult> CheckUsername([FromQuery] string username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length < 3)
        {
            return BadRequest(new { error = "Username must be at least 3 characters" });
        }

        var organizationId = GetOrganizationIdFromToken();
        var currentUserId = GetUserIdFromToken();
        
        var existingUser = await _userRepository.GetByUsernameAsync(organizationId, username);
        var available = existingUser == null || existingUser.UserId == currentUserId;

        return Ok(new
        {
            username,
            available
        });
    }

    private Guid GetUserIdFromToken()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
        
        if (Guid.TryParse(userIdClaim, out var userId))
        {
            return userId;
        }
        return Guid.Empty;
    }

    private Guid GetOrganizationIdFromToken()
    {
        var orgIdClaim = User.FindFirst("tenant_id")?.Value
            ?? User.FindFirst("organization_id")?.Value;
        
        if (Guid.TryParse(orgIdClaim, out var orgId))
        {
            return orgId;
        }
        return Guid.Empty;
    }
}

// ============================================
// Request DTOs
// ============================================

public class RegisterUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
}

public class UpdateProfileRequest
{
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
}
