using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.Search;
using JIM.Models.Security;
using JIM.Models.Staging;
namespace JIM.Data.Repositories;

public interface ISeedingRepository
{
    /// <summary>
    /// Creates all seed data in a single transaction.
    /// ServiceSettings is created LAST to ensure atomicity - if the process crashes during seeding,
    /// the absence of ServiceSettings will trigger a fresh seeding attempt on restart.
    /// </summary>
    public Task SeedDataAsync(
        List<MetaverseAttribute> metaverseAttributes,
        List<MetaverseObjectType> metaverseObjectTypes,
        List<PredefinedSearch> predefinedSearches,
        List<Role> roles,
        List<ExampleDataSet> exampleDataSets,
        List<DataGenerationTemplate> dataGenerationTemplates,
        List<ConnectorDefinition> connectorDefinitions);
}