using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.Search;
using JIM.Models.Security;

namespace JIM.Data.Repositories
{
    public interface ISeedingRepository
    {
        public Task SeedDataAsync(
            List<MetaverseAttribute> metaverseAttributes, 
            List<MetaverseObjectType> metaverseObjectTypes, 
            List<PredefinedSearch> predefinedSearches,
            List<Role> roles, 
            List<ExampleDataSet> exampleDataSets, 
            List<DataGenerationTemplate> dataGenerationTemplates);
    }
}
