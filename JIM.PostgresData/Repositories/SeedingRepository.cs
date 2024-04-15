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
    /// Creates data needed by the application to run.
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
        var changes = false;
        if (metaverseAttributes.Count > 0)
        {
            Repository.Database.MetaverseAttributes.AddRange(metaverseAttributes);
            Log.Information($"SeedDataAsync: Created {metaverseAttributes.Count} MetaverseAttributes");
            changes = true;
        }

        if (metaverseObjectTypes.Count > 0)
        {
            Repository.Database.MetaverseObjectTypes.AddRange(metaverseObjectTypes);
            Log.Information($"SeedDataAsync: Created {metaverseObjectTypes.Count} MetaverseObjectTypes");
            changes = true;
        }

        if (predefinedSearches.Count > 0)
        {
            Repository.Database.PredefinedSearches.AddRange(predefinedSearches);
            Log.Information($"SeedDataAsync: Created {predefinedSearches.Count} PredefinedSearches");
            changes = true;
        }

        if (roles.Count > 0)
        {
            Repository.Database.Roles.AddRange(roles);
            Log.Information($"SeedDataAsync: Created {roles.Count} Roles");
            changes = true;
        }

        if (exampleDataSets.Count > 0)
        {
            Repository.Database.ExampleDataSets.AddRange(exampleDataSets);
            Log.Information($"SeedDataAsync: Created {exampleDataSets.Count} ExampleDataSets");
            changes = true;
        }

        if (dataGenerationTemplates.Count > 0)
        {
            Repository.Database.DataGenerationTemplates.AddRange(dataGenerationTemplates);
            Log.Information($"SeedDataAsync: Created {dataGenerationTemplates.Count} DataGenerationTemplates");
            changes = true;
        }

        if (connectorDefinitions.Count > 0)
        {
            Repository.Database.ConnectorDefinitions.AddRange(connectorDefinitions);
            Log.Information($"SeedDataAsync: Created {connectorDefinitions.Count} ConnectorDefinitions");
            changes = true;
        }

        if (changes)
            await Repository.Database.SaveChangesAsync();         
    }
}
