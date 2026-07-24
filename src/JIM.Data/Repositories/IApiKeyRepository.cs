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
