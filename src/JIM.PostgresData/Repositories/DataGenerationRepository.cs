using JIM.Data.Repositories;
using JIM.Models.DataGeneration;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace JIM.PostgresData.Repositories
{
    public class DataGenerationRepository : IDataGenerationRepository
    {
        private PostgresDataRepository Repository { get; }

        internal DataGenerationRepository(PostgresDataRepository dataRepository)
        {
            Repository = dataRepository;
        }

        public List<DataGenerationTemplate> GetTemplates()
        {
            return Repository.Database.DataGenerationTemplates.Include(q => q.ObjectTypes).OrderBy(q => q.Name).ToList();
        }

        public async Task CreateTemplateAsync(DataGenerationTemplate template)
        {
            Repository.Database.DataGenerationTemplates.Add(template);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task UpdateTemplateAsync(DataGenerationTemplate template)
        {
            await Repository.Database.SaveChangesAsync();
        }

        public async Task DeleteTemplateAsync(int templateId)
        {
            var template = await Repository.Database.DataGenerationTemplates.SingleOrDefaultAsync(q => q.Id == templateId);
            if (template == null)
            {
                Log.Warning("DeleteTemplateAsync: No such template found to delete.");
                return;
            }
            
            // go through the template tree and remove all descendant template objects
            // cascade delete not used here due to references to non-template objects we definately don't want to delete
            foreach (var objectType in template.ObjectTypes)
                Repository.Database.DataGenerationTemplateAttributes.RemoveRange(objectType.TemplateAttributes);
            
            Repository.Database.DataGenerationObjectTypes.RemoveRange(template.ObjectTypes);
            Repository.Database.DataGenerationTemplates.Remove(template);
            await Repository.Database.SaveChangesAsync();
        }
    }
}
