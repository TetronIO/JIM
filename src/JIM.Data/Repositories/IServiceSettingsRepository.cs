using JIM.Models.Core;
namespace JIM.Data.Repositories;

public interface IServiceSettingsRepository
{
    // ServiceSettings (singleton) methods
    public Task<ServiceSettings?> GetServiceSettingsAsync();
    public Task<bool> ServiceSettingsExistAsync();
    public Task CreateServiceSettingsAsync(ServiceSettings serviceSettings);
    public Task UpdateServiceSettingsAsync(ServiceSettings serviceSettings);

    // ServiceSetting (individual settings) methods
    public Task<ServiceSetting?> GetSettingAsync(string key);
    public Task<List<ServiceSetting>> GetAllSettingsAsync();
    public Task<List<ServiceSetting>> GetSettingsByCategoryAsync(ServiceSettingCategory category);
    public Task<List<ServiceSetting>> GetOverriddenSettingsAsync();
    public Task CreateSettingAsync(ServiceSetting setting);
    public Task UpdateSettingAsync(ServiceSetting setting);
    public Task<bool> SettingExistsAsync(string key);
}