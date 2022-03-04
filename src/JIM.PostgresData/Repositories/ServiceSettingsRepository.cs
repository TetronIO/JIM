using JIM.Data;
using JIM.Models.Core;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace JIM.PostgresData.Repositories
{
    public class ServiceSettingsRepository : IServiceSettingsRepository
    {
        private PostgresDataRepository Repository { get; }

        internal ServiceSettingsRepository(PostgresDataRepository dataRepository)
        {
            Repository = dataRepository;
        }

        public ServiceSettings? GetServiceSettings()
        {
            try
            {
                return Repository.Database.ServiceSettings.Include(q => q.SSONameIDAttribute).FirstOrDefault();
            }
            catch (Npgsql.PostgresException ex)
            {
                if (ex.Message.StartsWith("42P01: relation \"ServiceSettings\" does not exist"))
                {
                    Log.Verbose("GetServiceSettings: Service Settings does not exist. We expect this in a new-db scenario where the app isn't ready yet.");
                    return null;
                }

                throw;
            }
        }

        public async Task UpdateServiceSettingsAsync(ServiceSettings serviceSettings)
        {
            var dbServiceSettings = Repository.Database.ServiceSettings.FirstOrDefault();
            if (dbServiceSettings == null)
            {
                Log.Error("UpdateServiceSettingsAsync: Could not retrieve a ServiceSettings object to update.");
                return;
            }

            // map scalar value updates to the db version of the object
            Repository.Database.Entry(dbServiceSettings).CurrentValues.SetValues(serviceSettings);

            // manually update reference properties
            dbServiceSettings.SSONameIDAttribute = serviceSettings.SSONameIDAttribute;

            await Repository.Database.SaveChangesAsync();
        }
    }
}