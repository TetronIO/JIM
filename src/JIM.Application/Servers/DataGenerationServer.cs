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

        public async Task<DataGenerationTemplate?> GetTemplateAsync(int id)
        {
            return await Application.Repository.DataGeneration.GetTemplateAsync(id);
        }

        public async Task<DataGenerationTemplate?> GetTemplateAsync(string name)
        {
            return await Application.Repository.DataGeneration.GetTemplateAsync(name);
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

        public async Task ExecuteTemplateAsync(int templateId)
        {
            // get the entire template 
            // enumerate the object types
            // build the objects << probably fine up to a point, then it might consume too much ram
            // submit in bulk to data layer << probably fine up to a point, then EF might blow a gasket

            var t = await GetTemplateAsync(templateId);
            if (t == null)
                throw new ArgumentException("No template found with that id");
            
            // object type dependency graph needs considering
            // for now we should probably just advise people to create template object types in reverse order to how they're referenced

            var metaverseObjectsToCreate = new List<MetaverseObject>();
            foreach (var ot in t.ObjectTypes)
            {
                for (var i = 0; i < ot.ObjectsToCreate; i++)
                {
                    var mo = new MetaverseObject();
                    mo.Type = ot.MetaverseObjectType;
                    
                    foreach (var ta in ot.TemplateAttributes)
                    {
                        // handle each attribute type in dedicated functions
                        switch (ta.MetaverseAttribute.type)
                        {
                                
                        }
                    }
                }
            }
            
            // submit metaverse objects to data layer for creation
        }
        #endregion
    }
}
