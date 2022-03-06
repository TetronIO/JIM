using JIM.Models.DataGeneration;

namespace JIM.Data.Repositories
{
    public interface IDataGenerationRepository
    {
        public List<DataGenerationTemplate> GetTemplates();

        public Task CreateTemplateAsync(DataGenerationTemplate template);

        public Task UpdateTemplateAsync(DataGenerationTemplate template);

        public Task DeleteTemplateAsync(int templateId);
    }
}
