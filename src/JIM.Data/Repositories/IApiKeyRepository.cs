// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
    /// Gets the keys scoped to a Connected System, whether or not they can be used now, ordered by name.
    /// </summary>
    Task<List<ApiKey>> GetByScopeConnectedSystemAsync(int connectedSystemId);

    /// <summary>
    /// Counts the keys scoped to a Connected System that can be used now: enabled, and not expired at
    /// <paramref name="asAt"/>.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System the keys are scoped to.</param>
    /// <param name="asAt">The time expiry is judged against.</param>
    /// <param name="excludingId">A key to leave out of the count, such as the one being updated.</param>
    Task<int> CountActiveScopedKeysAsync(int connectedSystemId, DateTime asAt, Guid? excludingId = null);

    /// <summary>
    /// Deletes an API key.
    /// </summary>
    Task DeleteAsync(Guid id);

    /// <summary>
    /// Records usage of an API key (updates last used timestamp and IP). Best-effort, throttled
    /// bookkeeping: implementations persist at most one stamp per key per short interval (see the
    /// implementation's <c>UsageStampInterval</c>), so continuous API polling does not translate
    /// into a write per request; LastUsedAt is a coarse recently-used indicator, not a request log.
    /// </summary>
    Task RecordUsageAsync(Guid id, string? ipAddress);
}
