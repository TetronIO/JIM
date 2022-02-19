using TIM.Models.Core;

namespace TIM.Data
{
    public interface IServiceSettingsRepository
    {
        public ServiceSettings GetServiceSettings();
        public Task UpdateServiceSettingsAsync(ServiceSettings serviceSettings);
    }
}
