// Chat.Api/Repositories/UserRepository.cs
using Chat.Api.Models;

namespace Chat.Api.Repositories;

public class CassandraUserRepository : IUserRepository
{
    private readonly Cassandra.ISession _session;
    private readonly ILogger<CassandraUserRepository> _logger;

    // Prepared statements para melhor performance
    private readonly Lazy<Cassandra.PreparedStatement> _selectByIdStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _selectByUsernameStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _selectByEmailStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _insertUserStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _insertUserByUsernameStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _insertUserByEmailStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _selectByOrgStmt;

    public CassandraUserRepository(Cassandra.ISession session, ILogger<CassandraUserRepository> logger)
    {
        _session = session;
        _logger = logger;

        // Preparar statements (lazy para não bloquear construtor)
        _selectByIdStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare("SELECT * FROM users WHERE user_id = ?"));

        _selectByUsernameStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare("SELECT * FROM users_by_username WHERE organization_id = ? AND username = ?"));

        _selectByEmailStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare("SELECT * FROM users_by_email WHERE email = ?"));

        _insertUserStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare(@"
                INSERT INTO users (user_id, organization_id, username, display_name, email, avatar_url, created_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?)"));

        _insertUserByUsernameStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare(@"
                INSERT INTO users_by_username (organization_id, username, user_id, display_name, email, avatar_url)
                VALUES (?, ?, ?, ?, ?, ?)"));

        _insertUserByEmailStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare(@"
                INSERT INTO users_by_email (email, user_id, organization_id, username, display_name)
                VALUES (?, ?, ?, ?, ?)"));

        _selectByOrgStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare("SELECT * FROM users_by_username WHERE organization_id = ? LIMIT ?"));
    }

    public async Task<User?> GetByIdAsync(Guid userId)
    {
        try
        {
            var bound = _selectByIdStmt.Value.Bind(userId);
            var result = await _session.ExecuteAsync(bound);
            var row = result.FirstOrDefault();

            if (row == null) return null;

            return MapRowToUser(row);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user by ID: {UserId}", userId);
            throw;
        }
    }

    public async Task<User?> GetByUsernameAsync(Guid organizationId, string username)
    {
        try
        {
            var normalizedUsername = username.ToLowerInvariant().Trim();
            var bound = _selectByUsernameStmt.Value.Bind(organizationId, normalizedUsername);
            var result = await _session.ExecuteAsync(bound);
            var row = result.FirstOrDefault();

            if (row == null) return null;

            return new User
            {
                UserId = row.GetValue<Guid>("user_id"),
                OrganizationId = organizationId,
                Username = row.GetValue<string>("username"),
                DisplayName = row.GetValue<string>("display_name"),
                Email = row.GetValue<string>("email"),
                AvatarUrl = row.GetValue<string>("avatar_url")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user by username: {Username}", username);
            throw;
        }
    }

    public async Task<User?> GetByEmailAsync(string email)
    {
        try
        {
            var normalizedEmail = email.ToLowerInvariant().Trim();
            var bound = _selectByEmailStmt.Value.Bind(normalizedEmail);
            var result = await _session.ExecuteAsync(bound);
            var row = result.FirstOrDefault();

            if (row == null) return null;

            // Buscar dados completos pelo ID
            var userId = row.GetValue<Guid>("user_id");
            return await GetByIdAsync(userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user by email: {Email}", email);
            throw;
        }
    }

    public async Task<IEnumerable<UserSearchResult>> SearchAsync(Guid organizationId, string query, int limit = 20)
    {
        try
        {
            // Cassandra não suporta LIKE nativo, então buscamos todos da org e filtramos
            // Em produção, usaríamos Elasticsearch ou Cassandra SASI index
            var normalizedQuery = query.ToLowerInvariant().Trim();

            var bound = _selectByOrgStmt.Value.Bind(organizationId, limit * 5); // Busca mais para filtrar
            var result = await _session.ExecuteAsync(bound);

            var users = new List<UserSearchResult>();
            foreach (var row in result)
            {
                var username = row.GetValue<string>("username") ?? "";
                var displayName = row.GetValue<string>("display_name") ?? "";

                // Filtrar por query (case-insensitive)
                if (username.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                    displayName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                {
                    users.Add(new UserSearchResult
                    {
                        UserId = row.GetValue<Guid>("user_id"),
                        Username = username,
                        DisplayName = displayName,
                        Email = row.GetValue<string>("email"),
                        AvatarUrl = row.GetValue<string>("avatar_url")
                    });

                    if (users.Count >= limit) break;
                }
            }

            return users;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching users: {Query}", query);
            throw;
        }
    }

    public async Task<User> UpsertAsync(User user)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (user.CreatedAt == default)
            {
                user.CreatedAt = now;
            }
            user.UpdatedAt = now;

            var normalizedUsername = user.Username.ToLowerInvariant().Trim();
            var normalizedEmail = user.Email?.ToLowerInvariant().Trim();

            // Usar batch para garantir consistência
            var batch = new Cassandra.BatchStatement();

            // Inserir na tabela principal
            batch.Add(_insertUserStmt.Value.Bind(
                user.UserId,
                user.OrganizationId,
                normalizedUsername,
                user.DisplayName,
                normalizedEmail,
                user.AvatarUrl,
                user.CreatedAt.UtcDateTime,
                user.UpdatedAt.UtcDateTime
            ));

            // Inserir no índice por username
            batch.Add(_insertUserByUsernameStmt.Value.Bind(
                user.OrganizationId,
                normalizedUsername,
                user.UserId,
                user.DisplayName,
                normalizedEmail,
                user.AvatarUrl
            ));

            // Inserir no índice por email (se tiver email)
            if (!string.IsNullOrEmpty(normalizedEmail))
            {
                batch.Add(_insertUserByEmailStmt.Value.Bind(
                    normalizedEmail,
                    user.UserId,
                    user.OrganizationId,
                    normalizedUsername,
                    user.DisplayName
                ));
            }

            await _session.ExecuteAsync(batch);

            _logger.LogInformation(
                "User upserted: UserId={UserId}, Username={Username}",
                user.UserId, normalizedUsername);

            return user;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error upserting user: {UserId}", user.UserId);
            throw;
        }
    }

    public async Task<bool> UsernameExistsAsync(Guid organizationId, string username)
    {
        var user = await GetByUsernameAsync(organizationId, username);
        return user != null;
    }

    public async Task<IEnumerable<UserSearchResult>> GetByOrganizationAsync(Guid organizationId, int limit = 100)
    {
        try
        {
            var bound = _selectByOrgStmt.Value.Bind(organizationId, limit);
            var result = await _session.ExecuteAsync(bound);

            var users = new List<UserSearchResult>();
            foreach (var row in result)
            {
                users.Add(new UserSearchResult
                {
                    UserId = row.GetValue<Guid>("user_id"),
                    Username = row.GetValue<string>("username") ?? "",
                    DisplayName = row.GetValue<string>("display_name") ?? "",
                    Email = row.GetValue<string>("email"),
                    AvatarUrl = row.GetValue<string>("avatar_url")
                });
            }

            return users;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting users by organization: {OrgId}", organizationId);
            throw;
        }
    }

    private User MapRowToUser(Cassandra.Row row)
    {
        return new User
        {
            UserId = row.GetValue<Guid>("user_id"),
            OrganizationId = row.GetValue<Guid>("organization_id"),
            Username = row.GetValue<string>("username") ?? "",
            DisplayName = row.GetValue<string>("display_name") ?? "",
            Email = row.GetValue<string>("email"),
            AvatarUrl = row.GetValue<string>("avatar_url"),
            CreatedAt = row.GetValue<DateTimeOffset>("created_at"),
            UpdatedAt = row.GetValue<DateTimeOffset>("updated_at")
        };
    }
}