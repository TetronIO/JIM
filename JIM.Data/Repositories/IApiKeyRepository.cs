using JIM.Models.Security;

namespace JIM.Data.Repositories;

/// <summary>
/// Repository interface for API key operations.
/// </summary>
public interface IApiKeyRepository
{
    /// <summary>
    /// Gets all API keys.
    /// </summary>
    Task<List<ApiKey>> GetAllAsync();

    /// <summary>
    /// Gets an API key by its ID.
    /// </summary>
    Task<ApiKey?> GetByIdAsync(Guid id);

    /// <summary>
    /// Gets an API key by its hash. Used for authentication.
    /// </summary>
    Task<ApiKey?> GetByHashAsync(string keyHash);

    /// <summary>
    /// Creates a new API key.
    /// </summary>
    Task<ApiKey> CreateAsync(ApiKey apiKey);

    /// <summary>
    /// Updates an existing API key.
    /// </summary>
    Task<ApiKey> UpdateAsync(ApiKey apiKey);

    /// <summary>
    /// Deletes an API key.
    /// </summary>
    Task DeleteAsync(Guid id);

    /// <summary>
    /// Records usage of an API key (updates last used timestamp and IP).
    /// </summary>
    Task RecordUsageAsync(Guid id, string? ipAddress);
}
