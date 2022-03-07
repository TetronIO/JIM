using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Security;
using Serilog;

namespace JIM.PostgresData.Repositories
{
    public class SeedingRepository : ISeedingRepository
    {
        private PostgresDataRepository Repository { get; }

        internal SeedingRepository(PostgresDataRepository dataRepository)
        {
            Repository = dataRepository;
        }

        /// <summary>
        /// Creates data needed by the application to run.
        /// Does not perform existence checks, you need to do this before calling this method.
        /// </summary>
        public async Task SeedDataAsync(List<MetaverseAttribute> metaverseAttributes, List<MetaverseObjectType> metaverseObjectTypes, List<Role> roles)
        {
            Repository.Database.MetaverseAttributes.AddRange(metaverseAttributes);
            Repository.Database.MetaverseObjectTypes.AddRange(metaverseObjectTypes);
            Repository.Database.Roles.AddRange(roles);
            await Repository.Database.SaveChangesAsync();
            Log.Information($"SeedDataAsync: Created {metaverseAttributes.Count} MetaverseAttributes");
            Log.Information($"SeedDataAsync: Created {metaverseObjectTypes.Count} MetaverseObjectTypes");
            Log.Information($"SeedDataAsync: Created {roles.Count} Roles");
        }
    }
}
