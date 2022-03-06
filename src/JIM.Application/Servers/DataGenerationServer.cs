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

        public List<DataGenerationTemplate> GetTemplates()
        {
            return Application.Repository.DataGeneration.GetTemplates();
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
    }
}
