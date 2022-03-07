using JIM.Models.Core;

namespace JIM.Application.Servers
{
    public class ServiceSettingsServer
    {
        private JimApplication Application { get; }

        internal ServiceSettingsServer(JimApplication application)
        {
            Application = application;
        }

        internal async Task<bool> ServiceSettingsExistAsync()
        {
            return await Application.Repository.ServiceSettings.ServiceSettingsExistAsync();
        }

        public ServiceSettings? GetServiceSettings()
        {
            return Application.Repository.ServiceSettings.GetServiceSettings();
        }

        internal async Task CreateServiceSettingsAsync(ServiceSettings serviceSettings)
        {
            await Application.Repository.ServiceSettings.CreateServiceSettingsAsync(serviceSettings);
        }

        public async Task UpdateServiceSettingsAsync(ServiceSettings serviceSettings)
        {
            await Application.Repository.ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
        }
    }
}