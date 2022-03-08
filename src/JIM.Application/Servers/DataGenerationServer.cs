using JIM.Models.DataGeneration;

namespace JIM.Application.Servers
{
    public class DataGenerationServer
    {
        private JimApplication Application { get; }

        internal DataGenerationServer(JimApplication application)
        {
            Application = application;
        }

        #region ExampleDataSets
        public async Task<List<ExampleDataSet>> GetExampleDataSetsAsync()
        {
            return await Application.Repository.DataGeneration.GetExampleDataSetsAsync();
        }

        public async Task<ExampleDataSet?> GetExampleDataSetAsync(string name, string culture)
        {
            return await Application.Repository.DataGeneration.GetExampleDataSetAsync(name, culture);
        }

        public async Task CreateExampleDataSetAsync(ExampleDataSet exampleDataSet)
        {
            await Application.Repository.DataGeneration.CreateExampleDataSetAsync(exampleDataSet);
        }

        public async Task UpdateExampleDataSetAsync(ExampleDataSet exampleDataSet)
        {
            await Application.Repository.DataGeneration.UpdateExampleDataSetAsync(exampleDataSet);
        }

        public async Task DeleteExampleDataSetAsync(int exampleDataSetId)
        {
            await Application.Repository.DataGeneration.DeleteExampleDataSetAsync(exampleDataSetId);
        }
        #endregion

        #region DataGenerationTemplates
        public async Task<List<DataGenerationTemplate>> GetTemplatesAsync()
        {
            return await Application.Repository.DataGeneration.GetTemplatesAsync();
        }

        public async Task CreateTemplateAsync(DataGenerationTemplate template)
        {
            await Application.Repository.DataGeneration.CreateTemplateAsync(template);
        }

        public async Task UpdateTemplateAsync(DataGenerationTemplate template)
        {
            await Application.Repository.DataGeneration.UpdateTemplateAsync(template);
        }

        public async Task DeleteTemplateAsync(int templateId)
        {
            await Application.Repository.DataGeneration.DeleteTemplateAsync(templateId);
        }
        #endregion
    }
}
