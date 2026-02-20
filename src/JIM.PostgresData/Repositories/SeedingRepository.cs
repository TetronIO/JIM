using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.Search;
using JIM.Models.Security;
using JIM.Models.Staging;
using Serilog;
namespace JIM.PostgresData.Repositories;

public class SeedingRepository : ISeedingRepository
{
    private PostgresDataRepository Repository { get; }

    internal SeedingRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    /// <summary>
    /// Creates all seed data in a single transaction.
    /// ServiceSettings is created LAST to ensure atomicity - if the process crashes during seeding,
    /// the absence of ServiceSettings will trigger a fresh seeding attempt on restart.
    /// Does not perform existence checks, you need to do this before calling this method.
    /// </summary>
    public async Task SeedDataAsync(
        List<MetaverseAttribute> metaverseAttributes,
        List<MetaverseObjectType> metaverseObjectTypes,
        List<PredefinedSearch> predefinedSearches,
        List<Role> roles,
        List<ExampleDataSet> exampleDataSets,
        List<DataGenerationTemplate> dataGenerationTemplates,
        List<ConnectorDefinition> connectorDefinitions)
    {
        if (metaverseAttributes.Count > 0)
        {
            Repository.Database.MetaverseAttributes.AddRange(metaverseAttributes);
            Log.Information($"SeedDataAsync: Created {metaverseAttributes.Count} MetaverseAttributes");
        }

        if (metaverseObjectTypes.Count > 0)
        {
            Repository.Database.MetaverseObjectTypes.AddRange(metaverseObjectTypes);
            Log.Information($"SeedDataAsync: Created {metaverseObjectTypes.Count} MetaverseObjectTypes");
        }

        if (predefinedSearches.Count > 0)
        {
            Repository.Database.PredefinedSearches.AddRange(predefinedSearches);
            Log.Information($"SeedDataAsync: Created {predefinedSearches.Count} PredefinedSearches");
        }

        if (roles.Count > 0)
        {
            Repository.Database.Roles.AddRange(roles);
            Log.Information($"SeedDataAsync: Created {roles.Count} Roles");
        }

        if (exampleDataSets.Count > 0)
        {
            Repository.Database.ExampleDataSets.AddRange(exampleDataSets);
            Log.Information($"SeedDataAsync: Created {exampleDataSets.Count} ExampleDataSets");
        }

        if (dataGenerationTemplates.Count > 0)
        {
            Repository.Database.DataGenerationTemplates.AddRange(dataGenerationTemplates);
            Log.Information($"SeedDataAsync: Created {dataGenerationTemplates.Count} DataGenerationTemplates");
        }

        if (connectorDefinitions.Count > 0)
        {
            Repository.Database.ConnectorDefinitions.AddRange(connectorDefinitions);
            Log.Information($"SeedDataAsync: Created {connectorDefinitions.Count} ConnectorDefinitions");
        }

        // CRITICAL: ServiceSettings is created LAST in the same transaction.
        // This ensures that if the process crashes during seeding, ServiceSettings won't exist,
        // and the next startup will retry seeding from scratch.
        // This prevents a race condition where JIM.Web sees ServiceSettings exists but MetaverseAttributes don't.
        var serviceSettings = new ServiceSettings();
        Repository.Database.ServiceSettings.Add(serviceSettings);
        Log.Information("SeedDataAsync: Created ServiceSettings");

        await Repository.Database.SaveChangesAsync();
        Log.Information("SeedDataAsync: All seed data committed successfully");
    }
}
