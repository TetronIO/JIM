using JIM.Models.Core;
using JIM.Models.DataGeneration;
using Serilog;
using System.Diagnostics;
using System.Text.RegularExpressions;

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
            var objectPreparationStopwatch = new Stopwatch();
            totalTimeStopwatch.Start();
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
            var dataGenerationValueTrackers = new List<DataGenerationValueTracker>();

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
                                    GenerateMetaverseStringValue(metaverseObject, templateAttribute, random, dataGenerationValueTrackers);
                                    break;
                                case AttributeDataType.Guid:
                                    GenerateMetaverseGuidValue(metaverseObject, templateAttribute);
                                    break;
                                case AttributeDataType.Number:
                                    GenerateMetaverseNumberValue(metaverseObjectsToCreate, metaverseObject, templateAttribute, random, dataGenerationValueTrackers);
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

            // todo: ensure that attribute population percentage values are respected

            objectPreparationStopwatch.Stop();

            // submit metaverse objects to data layer for creation
            var persistenceStopwatch = new Stopwatch();
            persistenceStopwatch.Start();
            // todo: persist!
            persistenceStopwatch.Stop();

            totalTimeStopwatch.Stop();
            Log.Information($"ExecuteTemplateAsync: Template '{t.Name}' complete. {totalObjectsCreated} objects prepared in {objectPreparationStopwatch.Elapsed}. Persisted in {persistenceStopwatch.Elapsed}. Total time: {totalTimeStopwatch.Elapsed}");
        }

        private static void GenerateMetaverseStringValue(
            MetaverseObject metaverseObject, 
            DataGenerationTemplateAttribute dataGenerationTemplateAttribute,
            Random random,
            List<DataGenerationValueTracker> dataGenerationValueTrackers)
        {
            if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
                throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

            string output;
            if (dataGenerationTemplateAttribute.ExampleDataSets.Count == 1)
            {
                // single example-data set based
                var valueIndex = random.Next(0, dataGenerationTemplateAttribute.ExampleDataSets[0].Values.Count);
                output = dataGenerationTemplateAttribute.ExampleDataSets[0].Values[valueIndex].StringValue;
            }
            else if (dataGenerationTemplateAttribute.ExampleDataSets.Count > 1)
            {
                // multiple example-data set based:
                // just choose randomly a value from across the datasets. simplest for now
                // would prefer to end up with an even distribution of values from across the datasets, but as the kids say: "that's long bruv"
                var dataSetIndex = random.Next(0, dataGenerationTemplateAttribute.ExampleDataSets.Count - 1);
                var valueIndex = random.Next(0, dataGenerationTemplateAttribute.ExampleDataSets[dataSetIndex].Values.Count - 1);
                output = dataGenerationTemplateAttribute.ExampleDataSets[0].Values[valueIndex].StringValue;
            }
            else if (!string.IsNullOrEmpty(dataGenerationTemplateAttribute.Pattern))
            {
                // pattern generation:
                // parse out the attribute variables {var} and system variables [var]
                // use regex to do this. keep it simple for now, just replace what you find
                // later on we can look at encapsulation, i.e. functions around vars, and functions around functions.
                // replace attribute vars first, then check system vars, i.e. uniqueness ids against complete generated string.
                output = ReplaceAttributeVariables(metaverseObject, dataGenerationTemplateAttribute.Pattern);
                output = ReplaceSystemVariables(metaverseObject, dataGenerationTemplateAttribute.MetaverseAttribute, output, dataGenerationValueTrackers);
            }
            else
            {
                throw new InvalidDataException("DataGenerationTemplateAttribute configuration not as expected");
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

        private static void GenerateMetaverseNumberValue(
            List<MetaverseObject> metaverseObjects, 
            MetaverseObject metaverseObject, 
            DataGenerationTemplateAttribute dataGenerationTemplateAttribute, 
            Random random,
            List<DataGenerationValueTracker> dataGenerationValueTrackers)
        {
            // todo: make use of data gen value trackers to get next highest value
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
            foreach (Match match in regex.Matches(textToProcess))
            {
                // snip off the brackets: {} to get the attribute name, i.e FirstName
                var attributeName = match.Value.Substring(1, match.Value.Length -1);
                
                // find the attribute value on the Metaverse Object:
                var attribute = metaverseObject.AttributeValues.SingleOrDefault(q => q.Attribute.Name == attributeName);
                if (attribute == null)
                    throw new InvalidDataException($"AttributeValue not found for Attribute:  {attributeName}. Check your pattern. Check that you have added the DataGenerationTemplateAttribute before the pattern is defined.");
                
                textToProcess = textToProcess.Replace(match.Value, attribute.StringValue);
            }

            return textToProcess;
        }
        
        private static string ReplaceSystemVariables(
            MetaverseObject metaverseObject, 
            MetaverseAttribute metaverseAttribute, 
            string textToProcess,
            List<DataGenerationValueTracker> dataGenerationValueTrackers)
        {
            // match system variables
            // enumerate, process
            var regex = new Regex(@"(\[.*?\])", RegexOptions.Compiled);
            var systemVars = regex.Matches(textToProcess);            
            foreach (Match match in systemVars)
            {
                // snip off the brackets: {} to get the attribute name, i.e FirstName
                var variableName = match.Value[1..^1];
                
                // keeping these as strings for now. They will need evolving into part of the Functions feature at some point
                if (variableName == "UniqueInt")
                {
                    // is the string value unique amongst all MetaverseObjects of the same type?
                    // if so, replace the system variable with an empty string
                    // if not, add a uniqueness in in place of the system variable
                    
                    // get the text value without any unique int added, i.e. "joe.bloggs@demo.tetron.io"
                    var textWithoutSystemVar = textToProcess.Replace(match.Value, string.Empty);

                    // have we already generated this value, and therefore need to add a unique int to it?
                    var uniqueIntTracker = dataGenerationValueTrackers.SingleOrDefault(q => q.ObjectTypeId == metaverseObject.Type.Id && q.AttributeId == metaverseAttribute.Id && q.StringValue == textWithoutSystemVar);
                    if (uniqueIntTracker == null)
                    {
                        // this is a unique value, not previously assigned. it does not need a unique int added.
                        textToProcess = textWithoutSystemVar;

                        // add it to the tracker
                        dataGenerationValueTrackers.Add(new DataGenerationValueTracker { ObjectTypeId = metaverseObject.Type.Id, AttributeId = metaverseAttribute.Id, StringValue = textWithoutSystemVar, LastIntAssigned = 1 });
                    }
                    else
                    {
                        // this is not a unique value, we've generated it before. we need a unique int added.
                        // increase the tracker last int assigned value by one as well for next time we generate the same value again
                        uniqueIntTracker.LastIntAssigned += 1;
                        textToProcess = textToProcess.Replace(match.Value, uniqueIntTracker.LastIntAssigned.ToString());
                    }
                }
            }
            
            return textToProcess;
        }
        #endregion
    }
}
