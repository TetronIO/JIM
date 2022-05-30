using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.DataGeneration.Dto;

namespace JIM.Data.Repositories
{
    public interface IDataGenerationRepository
    {
        public Task<List<ExampleDataSet>> GetExampleDataSetsAsync();
        public Task<ExampleDataSet?> GetExampleDataSetAsync(string name, string culture);
        public Task CreateExampleDataSetAsync(ExampleDataSet exampleDataSet);
        public Task UpdateExampleDataSetAsync(ExampleDataSet exampleDataSet);
        public Task DeleteExampleDataSetAsync(int exampleDataSetId);


        public Task<List<DataGenerationTemplate>> GetTemplatesAsync();
        public Task<List<DataGenerationTemplateHeader>> GetTemplateHeadersAsync();
        public Task<DataGenerationTemplate?> GetTemplateAsync(string name);
        public Task<DataGenerationTemplate?> GetTemplateAsync(int id);
        public Task CreateTemplateAsync(DataGenerationTemplate template);
        public Task UpdateTemplateAsync(DataGenerationTemplate template);
        public Task DeleteTemplateAsync(int templateId);


        public Task CreateMetaverseObjectsAsync(List<MetaverseObject> metsaverseObjects);
    }
}
