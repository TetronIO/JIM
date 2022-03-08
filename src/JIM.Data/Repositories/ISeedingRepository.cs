using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.Security;

namespace JIM.Data.Repositories
{
    public interface ISeedingRepository
    {
        public Task SeedDataAsync(List<MetaverseAttribute> metaverseAttributes, List<MetaverseObjectType> metaverseObjectTypes, List<Role> roles, List<ExampleDataSet> exampleDataSets, List<DataGenerationTemplate> dataGenerationTemplates);
    }
}
