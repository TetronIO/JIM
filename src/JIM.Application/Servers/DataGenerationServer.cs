using JIM.Models.Core;
using JIM.Models.DataGeneration;
using Serilog;
using System.Diagnostics;

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

            var totalTimeStopwatch = new Stopwatch();
            totalTimeStopwatch.Start();
            var objectPreparationStopwatch = new Stopwatch();
            objectPreparationStopwatch.Start();
            var totalObjectsCreated = 0;
            var t = await GetTemplateAsync(templateId);
            if (t == null)
                throw new ArgumentException("No template found with that id");

            if (!t.IsValid())
                throw new InvalidDataException("Template is invalid. Please check that all attributes are valid.");

            // object type dependency graph needs considering
            // for now we should probably just advise people to add template object types in reverse order to how they're referenced

            var random = new Random();
            var metaverseObjectsToCreate = new List<MetaverseObject>();
            foreach (var objectType in t.ObjectTypes)
            {
                for (var i = 0; i < objectType.ObjectsToCreate; i++)
                {
                    var metaverseObject = new MetaverseObject { Type = objectType.MetaverseObjectType };
                    foreach (var templateAttribute in objectType.TemplateAttributes)
                    {
                        if (templateAttribute.MetaverseAttribute != null)
                        {
                            // handle each attribute type in dedicated functions
                            switch (templateAttribute.MetaverseAttribute.Type)
                            {
                                case AttributeDataType.String:
                                    GenerateMetaverseStringValue(metaverseObjectsToCreate, metaverseObject, templateAttribute);
                                    break;
                                case AttributeDataType.Guid:
                                    GenerateMetaverseGuidValue(metaverseObject, templateAttribute);
                                    break;
                                case AttributeDataType.Number:
                                    GenerateMetaverseNumberValue(metaverseObjectsToCreate, metaverseObject, templateAttribute, random);
                                    break;
                                case AttributeDataType.DateTime:
                                    GenerateMetaverseDateTimeValue(metaverseObject, templateAttribute, random);
                                    break;
                                case AttributeDataType.Bool:
                                    GenerateMetaverseBooleanValue(metaverseObject, templateAttribute, random);
                                    break;
                            }
                        }

                        // todo: support Connector Space data generation
                    }

                    metaverseObjectsToCreate.Add(metaverseObject);
                    totalObjectsCreated++;
                }
            }

            objectPreparationStopwatch.Stop();

            var persistenceStopwatch = new Stopwatch();
            persistenceStopwatch.Start();

            // submit metaverse objects to data layer for creation
            persistenceStopwatch.Stop();

            totalTimeStopwatch.Stop();
            Log.Information($"ExecuteTemplateAsync: Template '{t.Name}' complete. {totalObjectsCreated} objects prepared in {objectPreparationStopwatch.Elapsed}. Persisted in {persistenceStopwatch.Elapsed}. Total time: {totalTimeStopwatch.Elapsed}");
        }

        private static void GenerateMetaverseStringValue(List<MetaverseObject> metaverseObjects, MetaverseObject metaverseObject, DataGenerationTemplateAttribute dataGenerationTemplateAttribute)
        {
            if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
                throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

            string output;
            if (dataGenerationTemplateAttribute.ExampleDataSets.Count > 0)
            {
                // example-data based:
                // if there are multiple data set references, use an equal amount from them over the entire object range
            } 
            else if (!string.IsNullOrEmpty(dataGenerationTemplateAttribute.Pattern))
            {
                // pattern generation:
                // parse out the attribute variables {var} and system variables [var]
                // use regex to do this. keep it simple for now, just replace what you find
                // later on we can look at encapsulation, i.e. functions around vars, and functions around functions.
                // replace attribute vars first, then check system vars, i.e. uniqueness ids against complete generated string.
                output = ReplaceAttributeVariables(metaverseObject, dataGenerationTemplateAttribute.Pattern);
                output = ReplaceSystemVariables(metaverseObjects, metaverseObject, output);
            }

            metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
                StringValue = output
            });
        }

        private static void GenerateMetaverseGuidValue(MetaverseObject metaverseObject, DataGenerationTemplateAttribute dataGenerationTemplateAttribute)
        {
            if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
                throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

            metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
                GuidValue = Guid.NewGuid()
            });
        }

        private static void GenerateMetaverseNumberValue(List<MetaverseObject> metaverseObjects, MetaverseObject metaverseObject, DataGenerationTemplateAttribute dataGenerationTemplateAttribute, Random random)
        {
            if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
                throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

            int value = 1;
            if (dataGenerationTemplateAttribute.RandomNumbers.HasValue && dataGenerationTemplateAttribute.RandomNumbers.Value)
            {
                // random numbers
                if (dataGenerationTemplateAttribute.MinNumber.HasValue && !dataGenerationTemplateAttribute.MaxNumber.HasValue)
                {
                    // min value only
                    value = random.Next(dataGenerationTemplateAttribute.MinNumber.Value, int.MaxValue);
                }
                else if (!dataGenerationTemplateAttribute.MinNumber.HasValue && dataGenerationTemplateAttribute.MaxNumber.HasValue)
                {
                    // max value only
                    value = random.Next(dataGenerationTemplateAttribute.MaxNumber.Value);
                }
                else if (dataGenerationTemplateAttribute.MinNumber.HasValue && dataGenerationTemplateAttribute.MaxNumber.HasValue)
                {
                    // min and max values
                    value = random.Next(dataGenerationTemplateAttribute.MinNumber.Value, dataGenerationTemplateAttribute.MaxNumber.Value);
                } 
            }
            else
            {
                // sequential numbers
                // query last value used for this object type and attribute. totally inefficient, but let's see what the performance is like first
                var highestCurrentValue = metaverseObjects.Where(mo => mo.Type == metaverseObject.Type).
                    SelectMany(q => q.AttributeValues.Where(av => av.Attribute == dataGenerationTemplateAttribute.MetaverseAttribute)).
                        Select(q => q.IntValue).Max();

                if (highestCurrentValue.HasValue)
                {
                    // we've already assigned a value, so just add one more to that
                    value += 1;
                }
                else
                {
                    // this is the first attribute we need to assign a sequential value too.
                    // if there's a minimum value we need to start from, use that, otherwise leave it at our starting point of 1.
                    if (dataGenerationTemplateAttribute.MinNumber.HasValue)
                        value = dataGenerationTemplateAttribute.MinNumber.Value;
                }
            }

            metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
                IntValue = value
            });
        }
        
        private static void GenerateMetaverseDateTimeValue(MetaverseObject metaverseObject, DataGenerationTemplateAttribute dataGenerationTemplateAttribute, Random random)
        {
            if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
                throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

            DateTime date;
            var startDate = DateTime.MinValue;
            var endDate = DateTime.MaxValue;
            if (dataGenerationTemplateAttribute.MinDate.HasValue && dataGenerationTemplateAttribute.MaxDate.HasValue)
            {
                // between two dates
                startDate = dataGenerationTemplateAttribute.MinDate.Value;
                endDate = dataGenerationTemplateAttribute.MaxDate.Value;
            }
            else if (dataGenerationTemplateAttribute.MinDate.HasValue && !dataGenerationTemplateAttribute.MaxDate.HasValue)
            {
                // just a min date
                startDate = dataGenerationTemplateAttribute.MinDate.Value;
            }
            else if (!dataGenerationTemplateAttribute.MinDate.HasValue && dataGenerationTemplateAttribute.MaxDate.HasValue)
            {
                // just a max date
                endDate = dataGenerationTemplateAttribute.MaxDate.Value;
            }

            var timeSpan = endDate - startDate;
            var newSpan = new TimeSpan(0, random.Next(0, (int)timeSpan.TotalMinutes), 0);
            date = startDate + newSpan;

            metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
                DateTimeValue = date
            });
        }
        
        private static void GenerateMetaverseBooleanValue(MetaverseObject metaverseObject, DataGenerationTemplateAttribute dataGenerationTemplateAttribute, Random random)
        {
            if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
                throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

            bool value;
            //if (dataGenerationTemplateAttribute.BoolTrueDistribution.HasValue)
            //{
                // a certain number of true values are required over the total number of objects created
                // todo: this, because, it's tired and I can't work this out atm
            //}
            //else
            //{
                // bool should be random
                value = Convert.ToBoolean(random.Next(0, 1));
            //}

            metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
                BoolValue = value
            });
        }
        #endregion

        #region String Generation
        private static string ReplaceAttributeVariables(MetaverseObject metaverseObject, string textToProcess)
        {
            // match attribute variables
            // enumerate, find their value and replace
            var regex = new Regex("({.*?})", RegexOptions.Compiled);
            foreach (var attributeVar in regex.Matches())
            {
                // snip off the brackets: {} to get the attribute name, i.e FirstName
                var attributeName = attributeVar.SubString(1, attributeVar.Length -1);
                
                // find the attribute value on the Metaverse Object:
                var attribute = metaverseObject.AttributeValues.SingleOrDefault(q => q.Attribute.Name == attributeName);
                if (attribute == null)
                    throw new InvalidDataException($"AttributeValue not found for Attribute:  {attributeName}. Check your pattern. Check that you have added the DataGenerationTemplateAttribute before the pattern is defined.");
                
                textToProcess = textToProcess.Replace(attributeVar, attribute.StringValue);
            }
            
            return textToProcess
        }
        
        private static string ReplaceSystemVariables(List<MetaverseObject> metaverseObjects, MetaverseAttribute metaverseAttribute, string textToProcess)
        {
            // match system variables
            // enumerate, process
            var regex = new Regex("(\[.*?\])", RegexOptions.Compiled);
            var systemVars = regex.Matches();            
            foreach (var systemVar in systemVars)
            {
                // snip off the brackets: {} to get the attribute name, i.e FirstName
                var variableName = systemVar.SubString(1, attributeVar.Length -1);
                
                // keeping these as strings for now. They will need evolving into part of the Functions feature at some point
                if (variableName == "UniqueInt")
                {
                    // is the string value unique amongst all MetaverseObjects of the same type?
                    // if so, replace the system variable with an empty string
                    // if not, add a uniqueness in in place of the system variable
                    
                    var alreadyAssignedStringValues = metaverseObjects.Where(mo => mo.Type == metaverseObject.Type).
                        SelectMany(q => q.AttributeValues.Where(av => av.Attribute == metaverseAttribute)).
                            SelectMany(q => q.StringValue);
                    
                    if (alreadyAssignedStringValues.Contains(q => q == string_without_system_var))
                    {
                        // this value has already been generated. it needs making unique with a unique int in place of the system var
                    }
                    else
                    {
                        // this value is unique among metaverse objects of the same type
                    }
                }
            }
        }
        #endregion
    }
}
