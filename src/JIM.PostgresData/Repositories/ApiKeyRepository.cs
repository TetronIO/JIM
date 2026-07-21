// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Security;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// PostgreSQL implementation of the API key repository.
/// </summary>
public class ApiKeyRepository : IApiKeyRepository
{
    private PostgresDataRepository Repository { get; }

    internal ApiKeyRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    public async Task<List<ApiKey>> GetAllAsync()
    {
        return await Repository.Database.ApiKeys
            .Include(ak => ak.Roles)
            .OrderBy(ak => ak.Name)
            .ToListAsync();
    }

    public async Task<ApiKey?> GetByIdAsync(Guid id)
    {
        return await Repository.Database.ApiKeys
            .Include(ak => ak.Roles)
            .SingleOrDefaultAsync(ak => ak.Id == id);
    }

    public async Task<ApiKey?> GetByHashAsync(string keyHash)
    {
        return await Repository.Database.ApiKeys
            .Include(ak => ak.Roles)
            .SingleOrDefaultAsync(ak => ak.KeyHash == keyHash);
    }

    public async Task<ApiKey> CreateAsync(ApiKey apiKey)
    {
        // Ensure we're attaching existing roles from the database
        if (apiKey.Roles.Count > 0)
        {
            var roleIds = apiKey.Roles.Select(r => r.Id).ToList();
            var existingRoles = await Repository.Database.Roles
                .AsTracking()
                .Where(r => roleIds.Contains(r.Id))
                .ToListAsync();
            apiKey.Roles = existingRoles;
        }

        Repository.Database.ApiKeys.Add(apiKey);
        await Repository.Database.SaveChangesAsync();
        return apiKey;
    }

    public async Task<ApiKey> UpdateAsync(ApiKey apiKey)
    {
        var existingKey = await Repository.Database.ApiKeys
            .Include(ak => ak.Roles)
            .AsTracking()
            .SingleOrDefaultAsync(ak => ak.Id == apiKey.Id)
            ?? throw new ArgumentException($"API key not found: {apiKey.Id}");

        existingKey.Name = apiKey.Name;
        existingKey.Description = apiKey.Description;
        existingKey.ExpiresAt = apiKey.ExpiresAt;
        existingKey.IsEnabled = apiKey.IsEnabled;

        // Copy the audit stamp too: callers (JIM.Web) query with NoTracking, so the instance the application layer
        // stamped is detached and these values would otherwise be silently dropped when this method re-loads the
        // tracked row.
        existingKey.LastUpdated = apiKey.LastUpdated;
        existingKey.LastUpdatedByType = apiKey.LastUpdatedByType;
        existingKey.LastUpdatedById = apiKey.LastUpdatedById;
        existingKey.LastUpdatedByName = apiKey.LastUpdatedByName;

        // Update roles - clear and re-add
        existingKey.Roles.Clear();
        if (apiKey.Roles.Count > 0)
        {
            var roleIds = apiKey.Roles.Select(r => r.Id).ToList();
            var newRoles = await Repository.Database.Roles
                .AsTracking()
                .Where(r => roleIds.Contains(r.Id))
                .ToListAsync();
            existingKey.Roles.AddRange(newRoles);
        }

        await Repository.Database.SaveChangesAsync();
        return existingKey;
    }

    public async Task DeleteAsync(Guid id)
    {
        var apiKey = await Repository.Database.ApiKeys
            .AsTracking()
            .SingleOrDefaultAsync(ak => ak.Id == id)
            ?? throw new ArgumentException($"API key not found: {id}");

        Repository.Database.ApiKeys.Remove(apiKey);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// The minimum interval between persisted usage stamps for one API key. LastUsedAt is a
    /// coarse "recently used" indicator, so sub-interval churn carries no information; throttling
    /// bounds the write volume when automation polls the API continuously (hundreds of requests
    /// per minute during integration runs) and prevents a hot-row convoy on the ApiKeys row when
    /// the database is briefly stalled by bulk synchronisation writes.
    /// </summary>
    public static readonly TimeSpan UsageStampInterval = TimeSpan.FromSeconds(30);

    public async Task RecordUsageAsync(Guid id, string? ipAddress)
    {
        var now = DateTime.UtcNow;
        var threshold = now - UsageStampInterval;

        if (Repository.Database.Database.IsRelational())
        {
            // Single set-based UPDATE with the throttle in the predicate: no SELECT round trip,
            // no tracked SaveChanges (so a transient timeout here cannot emit EF's
            // SaveChangesFailed error log for what callers treat as tolerated best-effort
            // bookkeeping), and concurrent stamps for the same key collapse to no-ops instead of
            // queueing writes on the row lock. Same relational/in-memory split as
            // ActivityRepository.IncrementAggregatedFailedAuthenticationAsync.
            await Repository.Database.ApiKeys
                .Where(ak => ak.Id == id && (ak.LastUsedAt == null || ak.LastUsedAt < threshold))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(ak => ak.LastUsedAt, now)
                    .SetProperty(ak => ak.LastUsedFromIp, ipAddress));
            return;
        }

        // The in-memory test provider does not support ExecuteUpdateAsync; fall back to a
        // tracked load/update/save with the same throttle semantics.
        var apiKey = await Repository.Database.ApiKeys
            .AsTracking()
            .SingleOrDefaultAsync(ak => ak.Id == id && (ak.LastUsedAt == null || ak.LastUsedAt < threshold));

        if (apiKey != null)
        {
            apiKey.LastUsedAt = now;
            apiKey.LastUsedFromIp = ipAddress;
            await Repository.Database.SaveChangesAsync();
        }
    }
}
