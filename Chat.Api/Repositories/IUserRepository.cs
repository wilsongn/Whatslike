// Chat.Api/Repositories/IUserRepository.cs
using Chat.Api.Models;

namespace Chat.Api.Repositories;

public interface IUserRepository
{
    /// <summary>
    /// Busca um usuário pelo ID
    /// </summary>
    Task<User?> GetByIdAsync(Guid userId);
    
    /// <summary>
    /// Busca um usuário pelo username dentro de uma organização
    /// </summary>
    Task<User?> GetByUsernameAsync(Guid organizationId, string username);
    
    /// <summary>
    /// Busca um usuário pelo email
    /// </summary>
    Task<User?> GetByEmailAsync(string email);
    
    /// <summary>
    /// Busca usuários por parte do username ou displayName
    /// </summary>
    Task<IEnumerable<UserSearchResult>> SearchAsync(Guid organizationId, string query, int limit = 20);
    
    /// <summary>
    /// Cria ou atualiza um usuário
    /// </summary>
    Task<User> UpsertAsync(User user);
    
    /// <summary>
    /// Verifica se um username já existe na organização
    /// </summary>
    Task<bool> UsernameExistsAsync(Guid organizationId, string username);
    
    /// <summary>
    /// Lista todos os usuários de uma organização
    /// </summary>
    Task<IEnumerable<UserSearchResult>> GetByOrganizationAsync(Guid organizationId, int limit = 100);
}
