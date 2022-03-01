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

        public ServiceSettings? GetServiceSettings()
        {
            return Application.Repository.ServiceSettings.GetServiceSettings();
        }

        public async Task UpdateServiceSettingsAsync(ServiceSettings serviceSettings)
        {
            await Application.Repository.ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
        }
    }
}