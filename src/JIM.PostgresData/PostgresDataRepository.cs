using JIM.Data;
using JIM.Data.Repositories;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Diagnostics;

namespace JIM.PostgresData
{
    public class PostgresDataRepository : IRepository, IDisposable
    {
        public IConnectedSystemRepository ConnectedSystems { get; }
        public IMetaverseRepository Metaverse { get; }
        public IServiceSettingsRepository ServiceSettings { get; }
        public ISecurityRepository Security { get; }
        public IDataGenerationRepository DataGeneration { get; }
        public ISeedingRepository Seeding { get; }
        public ITaskingRepository Tasking { get; }

        internal JimDbContext Database { get; }


        public PostgresDataRepository()
        {
            // needed to enable DateTime.Now assignments to work. Without it, the database will
            // throw errors when trying to set dates. This is a .NET/Postgres type mapping issue.
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            ConnectedSystems = new ConnectedSystemRepository(this);
            Metaverse = new MetaverseRepository(this);
            ServiceSettings = new ServiceSettingsRepository(this);
            Security = new SecurityRepository(this);
            DataGeneration = new DataGenerationRepository(this);
            Seeding = new SeedingRepository(this);
            Tasking = new TaskingRepository(this);
            Database = new JimDbContext();
        }

        public async Task InitialiseDatabaseAsync()
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            await MigrateDatabaseAsync();
            stopwatch.Stop();
            Log.Verbose($"InitialiseDatabaseAsync: Completed in: {stopwatch.Elapsed}");
        }

        public async Task InitialisationCompleteAsync()
        {
            // when the database is created, it's done so in maintenance mode
            // now we're all done, take it out of maintenance mode to open the app up to requests
            var serviceSettings = await ServiceSettings.GetServiceSettingsAsync();
            if (serviceSettings == null)
                throw new Exception("ServiceSettings is null. Something has gone wrong with seeding.");

            serviceSettings.IsServiceInMaintenanceMode = false;
            await ServiceSettings.UpdateServiceSettingsAsync(serviceSettings);
            Log.Verbose($"TakeServiceOutOfMaintenanceModeAsync: Done");
        }

        private async Task MigrateDatabaseAsync()
        {
            if (Database.Database.GetPendingMigrations().Any())
                await Database.Database.MigrateAsync();
        }

        public void Dispose()
        {
            if (Database != null)
                Database.Dispose();
        }
    }
}
