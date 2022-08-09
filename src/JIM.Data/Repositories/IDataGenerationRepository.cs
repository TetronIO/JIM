using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.DataGeneration.Dto;

namespace JIM.Data.Repositories
{
    public interface IDataGenerationRepository
    {
        public Task<List<ExampleDataSet>> GetExampleDataSetsAsync();
        public Task<List<ExampleDataSetHeader>> GetExampleDataSetHeadersAsync();
        public Task<ExampleDataSet?> GetExampleDataSetAsync(string name, string culture);
        public Task<ExampleDataSet?> GetExampleDataSetAsync(int id);
        public Task CreateExampleDataSetAsync(ExampleDataSet exampleDataSet);
        public Task UpdateExampleDataSetAsync(ExampleDataSet exampleDataSet);
        public Task DeleteExampleDataSetAsync(int exampleDataSetId);

        public Task<List<DataGenerationTemplate>> GetTemplatesAsync();
        public Task<List<DataGenerationTemplateHeader>> GetTemplateHeadersAsync();
        /// <summary>
        /// Retrieves a Data Generation Template.
        /// </summary>
        /// <param name="name">The name of the template to retrieve</param>
        /// <param name="retrieveValues">Control whether or not example data set values are returned as well. This is normally only needed when executing the template</param>
        public Task<DataGenerationTemplate?> GetTemplateAsync(string name, bool retrieveValues);
        /// <summary>
        /// Retrieves a Data Generation Template.
        /// </summary>
        /// <param name="id">The id of the template to retrieve</param>
        /// <param name="retrieveValues">Control whether or not example data set values are returned as well. This is normally only needed when executing the template</param>
        public Task<DataGenerationTemplate?> GetTemplateAsync(int id, bool retrieveValues);
        public Task CreateTemplateAsync(DataGenerationTemplate template);
        public Task UpdateTemplateAsync(DataGenerationTemplate template);
        public Task DeleteTemplateAsync(int templateId);

        public Task CreateMetaverseObjectsAsync(List<MetaverseObject> metsaverseObjects);
    }
}
