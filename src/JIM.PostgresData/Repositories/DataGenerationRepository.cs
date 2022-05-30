using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.DataGeneration.Dto;
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

        #region ExampleDataSets
        public async Task<List<ExampleDataSet>> GetExampleDataSetsAsync()
        {
            return await Repository.Database.ExampleDataSets.Include(q => q.Values).OrderBy(q => q.Name).ToListAsync();
        }

        public async Task<ExampleDataSet?> GetExampleDataSetAsync(string name, string culture)
        {
            return await Repository.Database.ExampleDataSets.Include(q => q.Values).SingleOrDefaultAsync(q => q.Name == name && q.Culture == culture);
        }

        public async Task CreateExampleDataSetAsync(ExampleDataSet exampleDataSet)
        {
            Repository.Database.ExampleDataSets.Add(exampleDataSet);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task UpdateExampleDataSetAsync(ExampleDataSet exampleDataSet)
        {
            await Repository.Database.SaveChangesAsync();
        }

        public async Task DeleteExampleDataSetAsync(int exampleDataSetId)
        {
            var exampleDataSet = await Repository.Database.ExampleDataSets.Include(q => q.Values).SingleOrDefaultAsync(q => q.Id == exampleDataSetId);
            if (exampleDataSet == null)
            {
                Log.Warning("DeleteExampleDataSetAsync: No such ExampleDetaSet found to delete.");
                return;
            }

            Repository.Database.ExampleDataSetValues.RemoveRange(exampleDataSet.Values);
            Repository.Database.ExampleDataSets.Remove(exampleDataSet);
            await Repository.Database.SaveChangesAsync();
        }
        #endregion

        #region DataGenerationTemplates
        public async Task<List<DataGenerationTemplate>> GetTemplatesAsync()
        {
            var templates = await Repository.Database.DataGenerationTemplates.
                Include(t => t.ObjectTypes).
                ThenInclude(ot => ot.MetaverseObjectType).
                Include(t => t.ObjectTypes).
                ThenInclude(o => o.TemplateAttributes).
                ThenInclude(ta => ta.MetaverseAttribute).
                Include(t => t.ObjectTypes).
                ThenInclude(o => o.TemplateAttributes).
                ThenInclude(ta => ta.ConnectedSystemAttribute).
                Include(t => t.ObjectTypes).
                ThenInclude(o => o.TemplateAttributes).
                ThenInclude(ta => ta.ExampleDataSetInstances).
                ThenInclude(edsi => edsi.ExampleDataSet).
                ThenInclude(eds => eds.Values).
                OrderBy(t => t.Name).ToListAsync();

            foreach (var t in templates)
                SortExampleDataSetInstances(t);

            return templates;
        }

        public async Task<List<DataGenerationTemplateHeader>> GetTemplateHeadersAsync()
        {
            var templates = await Repository.Database.DataGenerationTemplates.OrderBy(t => t.Name).Select(dgt => new DataGenerationTemplateHeader { 
                Name = dgt.Name,
                BuiltIn = dgt.BuiltIn,
                Created = dgt.Created,
                Id = dgt.Id}).ToListAsync();

            return templates;
        }

        public async Task<DataGenerationTemplate?> GetTemplateAsync(string name)
        {
            var t = await Repository.Database.DataGenerationTemplates.
                Include(t => t.ObjectTypes).
                ThenInclude(ot => ot.MetaverseObjectType).
                Include(t => t.ObjectTypes).
                ThenInclude(o => o.TemplateAttributes).
                ThenInclude(ta => ta.MetaverseAttribute).
                Include(t => t.ObjectTypes).
                ThenInclude(o => o.TemplateAttributes).
                ThenInclude(ta => ta.ConnectedSystemAttribute).
                Include(t => t.ObjectTypes).
                ThenInclude(o => o.TemplateAttributes).
                ThenInclude(ta => ta.ExampleDataSetInstances).
                ThenInclude(edsi => edsi.ExampleDataSet).
                ThenInclude(eds => eds.Values).
                SingleOrDefaultAsync(t => t.Name == name);

            if (t == null)
                return null;

            SortExampleDataSetInstances(t);
            return t;
        }

        public async Task<DataGenerationTemplate?> GetTemplateAsync(int id)
        {
            var t = await Repository.Database.DataGenerationTemplates.
                Include(t => t.ObjectTypes).
                ThenInclude(ot => ot.MetaverseObjectType).
                Include(t => t.ObjectTypes).
                ThenInclude(o => o.TemplateAttributes).
                ThenInclude(ta => ta.MetaverseAttribute).
                Include(t => t.ObjectTypes).
                ThenInclude(o => o.TemplateAttributes).
                ThenInclude(ta => ta.ConnectedSystemAttribute).
                Include(t => t.ObjectTypes).
                ThenInclude(o => o.TemplateAttributes).
                ThenInclude(ta => ta.ReferenceMetaverseObjectTypes).
                Include(t => t.ObjectTypes).
                ThenInclude(o => o.TemplateAttributes).
                ThenInclude(ta => ta.ExampleDataSetInstances).
                ThenInclude(edsi => edsi.ExampleDataSet).
                ThenInclude(eds => eds.Values).
                SingleOrDefaultAsync(t => t.Id == id);

            if (t == null)
                return null;

            SortExampleDataSetInstances(t);
            return t;
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
            var template = await Repository.Database.DataGenerationTemplates.
                Include(t => t.ObjectTypes).
                ThenInclude(ot => ot.TemplateAttributes).
                SingleOrDefaultAsync(t => t.Id == templateId);
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

        private static void SortExampleDataSetInstances(DataGenerationTemplate template)
        {
            foreach (var ot in template.ObjectTypes)
                foreach (var ta in ot.TemplateAttributes)
                    if (ta.ExampleDataSetInstances != null && ta.ExampleDataSetInstances.Count > 0)
                        ta.ExampleDataSetInstances = ta.ExampleDataSetInstances.OrderBy(q => q.Order).ToList();
        }
        #endregion

        public async Task CreateMetaverseObjectsAsync(List<MetaverseObject> metsaverseObjects)
        {
            Log.Verbose("CreateMetaverseObjectsAsync: Starting to persist MetaverseObjects...");
            if (metsaverseObjects == null || metsaverseObjects.Count == 0)
                throw new ArgumentNullException(nameof(metsaverseObjects));

            Repository.Database.MetaverseObjects.AddRange(metsaverseObjects);
            await Repository.Database.SaveChangesAsync();
            Log.Verbose("CreateMetaverseObjectsAsync: Done");
        }
    }
}
