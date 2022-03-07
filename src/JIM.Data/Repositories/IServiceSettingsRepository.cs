using JIM.Models.Core;

namespace JIM.Data.Repositories
{
    public interface IServiceSettingsRepository
    {
        public ServiceSettings? GetServiceSettings();
        public Task<bool> ServiceSettingsExistAsync();
        public Task CreateServiceSettingsAsync(ServiceSettings serviceSettings);
        public Task UpdateServiceSettingsAsync(ServiceSettings serviceSettings);
    }
}
