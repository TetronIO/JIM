using JIM.Data;
using JIM.Models.Core;
using Serilog;

namespace JIM.PostgresData
{
    public class ServiceSettingsRepository : IServiceSettingsRepository
    {
        private PostgresDataRepository Repository { get; }

        internal ServiceSettingsRepository(PostgresDataRepository dataRepository)
        {
            Repository = dataRepository;
        }

        public ServiceSettings GetServiceSettings()
        {
            using var db = new JimDbContext();
            return db.ServiceSettings.First();
        }

        public async Task UpdateServiceSettingsAsync(ServiceSettings serviceSettings)
        {
            using var db = new JimDbContext();
            var dbServiceSettings = db.ServiceSettings.FirstOrDefault();
            if (dbServiceSettings == null)
            {
                Log.Error("UpdateServiceSettingsAsync: Could not retrieve a ServiceSettings object to update.");
                return;
            }

            db.Entry(dbServiceSettings).CurrentValues.SetValues(serviceSettings);
            await db.SaveChangesAsync();
        }
    }
}