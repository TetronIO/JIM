using JIM.Data.Repositories;
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

        public async Task<ServiceSettings?> GetServiceSettingsAsync()
        {
            try
            {
                return await Repository.Database.ServiceSettings.Include(q => q.SSOUniqueIdentifierMetaverseAttribute).FirstOrDefaultAsync();
            }
            catch (Npgsql.PostgresException ex)
            {
                if (ex.Message.StartsWith("42P01: relation \"ServiceSettings\" does not exist", StringComparison.CurrentCultureIgnoreCase))
                {
                    Log.Verbose("JIM.PostgresData: GetServiceSettingsAsync() - Service Settings does not exist. We expect this in a new-db scenario where the app isn't ready yet.");
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
            dbServiceSettings.SSOUniqueIdentifierMetaverseAttribute = serviceSettings.SSOUniqueIdentifierMetaverseAttribute;

            await Repository.Database.SaveChangesAsync();
        }

        public async Task CreateServiceSettingsAsync(ServiceSettings serviceSettings)
        {
            Repository.Database.ServiceSettings.Add(serviceSettings);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task<bool> ServiceSettingsExistAsync()
        {
            return await Repository.Database.ServiceSettings.AnyAsync();
        }
    }
}