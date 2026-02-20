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
            .SingleOrDefaultAsync(ak => ak.Id == apiKey.Id)
            ?? throw new ArgumentException($"API key not found: {apiKey.Id}");

        existingKey.Name = apiKey.Name;
        existingKey.Description = apiKey.Description;
        existingKey.ExpiresAt = apiKey.ExpiresAt;
        existingKey.IsEnabled = apiKey.IsEnabled;

        // Update roles - clear and re-add
        existingKey.Roles.Clear();
        if (apiKey.Roles.Count > 0)
        {
            var roleIds = apiKey.Roles.Select(r => r.Id).ToList();
            var newRoles = await Repository.Database.Roles
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
            .SingleOrDefaultAsync(ak => ak.Id == id)
            ?? throw new ArgumentException($"API key not found: {id}");

        Repository.Database.ApiKeys.Remove(apiKey);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task RecordUsageAsync(Guid id, string? ipAddress)
    {
        var apiKey = await Repository.Database.ApiKeys
            .SingleOrDefaultAsync(ak => ak.Id == id);

        if (apiKey != null)
        {
            apiKey.LastUsedAt = DateTime.UtcNow;
            apiKey.LastUsedFromIp = ipAddress;
            await Repository.Database.SaveChangesAsync();
        }
    }
}
