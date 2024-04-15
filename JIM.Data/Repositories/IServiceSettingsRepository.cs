using JIM.Models.Core;
namespace JIM.Data.Repositories;

public interface IServiceSettingsRepository
{
    public Task<ServiceSettings?> GetServiceSettingsAsync();
    public Task<bool> ServiceSettingsExistAsync();
    public Task CreateServiceSettingsAsync(ServiceSettings serviceSettings);
    public Task UpdateServiceSettingsAsync(ServiceSettings serviceSettings);
}