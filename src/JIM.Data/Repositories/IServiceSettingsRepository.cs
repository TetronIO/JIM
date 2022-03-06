using JIM.Models.Core;

namespace JIM.Data.Repositories
{
    public interface IServiceSettingsRepository
    {
        public ServiceSettings? GetServiceSettings();
        public Task UpdateServiceSettingsAsync(ServiceSettings serviceSettings);
    }
}
