using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.Utility;
using Serilog;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace JIM.Application.Servers
{
    public class DataGenerationServer
    {
        #region accessors
        private JimApplication Application { get; }
        #endregion

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

            Log.Information($"ExecuteTemplateAsync: Generating data...");
            var totalTimeStopwatch = new Stopwatch();
            var objectPreparationStopwatch = new Stopwatch();
            totalTimeStopwatch.Start();
            objectPreparationStopwatch.Start();
            var totalObjectsCreated = 0;
            var getTemplateStopwatch = Stopwatch.StartNew();
            var t = await GetTemplateAsync(templateId);
            getTemplateStopwatch.Stop();
            Log.Verbose($"ExecuteTemplateAsync: get template took: {getTemplateStopwatch.Elapsed}");

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
                Parallel.For(0, objectType.ObjectsToCreate,
                index =>
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
                                    GenerateMetaverseNumberValue(metaverseObject, templateAttribute, random, dataGenerationValueTrackers);
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

                    lock (metaverseObjectsToCreate)
                        metaverseObjectsToCreate.Add(metaverseObject);

                    Interlocked.Add(ref totalObjectsCreated, 1);
                });

                // user manager attributes need assigning after all users have been prepared
                var templateManagerAttribute = objectType.TemplateAttributes.SingleOrDefault(ta => 
                    ta.MetaverseAttribute != null && 
                    ta.MetaverseAttribute.Name == Constants.BuiltInAttributes.Manager);

                if (objectType.MetaverseObjectType.Name == Constants.BuiltInObjectTypes.User && templateManagerAttribute != null && templateManagerAttribute.MetaverseAttribute != null && templateManagerAttribute.ManagerDepthPercentage.HasValue)
                    GenerateManagerAssignments(metaverseObjectsToCreate, objectType, templateManagerAttribute, random);
            }

            // ensure that attribute population percentage values are respected
            // do this by assigning all attributes with values (done), then go and randomly delete the required amount
            RemoveUnecessaryAttributeValues(t, metaverseObjectsToCreate, random);
            Log.Information($"ExecuteTemplateAsync: Generated {metaverseObjectsToCreate.Count:N0} objects");
            objectPreparationStopwatch.Stop();

            // submit metaverse objects to data layer for creation
            var persistenceStopwatch = new Stopwatch();
            persistenceStopwatch.Start();
            await Application.Repository.DataGeneration.CreateMetaverseObjectsAsync(metaverseObjectsToCreate);
            persistenceStopwatch.Stop();
            totalTimeStopwatch.Stop();
            Log.Information($"ExecuteTemplateAsync: Template '{t.Name}' complete. {totalObjectsCreated:N0} objects prepared in {objectPreparationStopwatch.Elapsed}. Persisted in {persistenceStopwatch.Elapsed}. Total time: {totalTimeStopwatch.Elapsed}");

            // trying to help garbage collection along. data generation results in a lot of ram usage.
            metaverseObjectsToCreate = null;
            dataGenerationValueTrackers = null;
        }

        private static void GenerateMetaverseStringValue(
            MetaverseObject metaverseObject,
            DataGenerationTemplateAttribute dataGenerationTemplateAttribute,
            Random random,
            List<DataGenerationValueTracker> dataGenerationValueTrackers)
        {
            if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
                throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

            // a string attribute can have a string or number type value assigned
            if (dataGenerationTemplateAttribute.IsUsingStrings())
            {
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
                    var dataSetMaxValue = dataGenerationTemplateAttribute.ExampleDataSets.Count - 1;
                    var dataSetIndex = random.Next(0, dataSetMaxValue);
                    var valueIndexMaxValue = dataGenerationTemplateAttribute.ExampleDataSets[dataSetIndex].Values.Count - 1;
                    var valueIndex = random.Next(0, valueIndexMaxValue);
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
            else if (dataGenerationTemplateAttribute.IsUsingNumbers())
            {
                var numberValue = GenerateNumberValue(metaverseObject.Type, dataGenerationTemplateAttribute, random, dataGenerationValueTrackers);
                metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
                    StringValue = numberValue.ToString()
                });
            }
            else
            {
                throw new ArgumentException("dataGenerationTemplateAttribute isn't using strings or numbers on a string attribute type");
            }
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
            MetaverseObject metaverseObject,
            DataGenerationTemplateAttribute dataGenerationTemplateAttribute,
            Random random,
            List<DataGenerationValueTracker> dataGenerationValueTrackers)
        {
            // todo: make use of data gen value trackers to get next highest value
            if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
                throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

            var value = GenerateNumberValue(metaverseObject.Type, dataGenerationTemplateAttribute, random, dataGenerationValueTrackers);
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

        private void GenerateManagerAssignments(
            List<MetaverseObject> metaverseObjectsToCreate, 
            DataGenerationObjectType objectType,
            DataGenerationTemplateAttribute templateManagerAttribute,
            Random random)
        {
            // binary tree approach
            // - project users to new list
            // - create manager list and remove manager from user list
            // - create binary tree using managers
            // - navigate binary tree and assign manager attributes to user objects
            // - then work out how to gradually assign more subordinates from the non-managers list as you go deeper into the tree

            if (templateManagerAttribute == null)
                return;

            if (templateManagerAttribute.ManagerDepthPercentage == null)
                return;

            if (templateManagerAttribute.MetaverseAttribute == null)
                return;

            var users = metaverseObjectsToCreate.Where(mo => mo.Type == objectType.MetaverseObjectType).ToList();
            var managerTreePrepStopwatch = Stopwatch.StartNew();
            var managerAttribute = templateManagerAttribute.MetaverseAttribute;
            var managersNeeded = users.Count / 100 * templateManagerAttribute.ManagerDepthPercentage.Value;

            // randomly select managers and remove them from the users list so we have a list of managers and a list of potential direct reports
            var managers = new List<MetaverseObject>();
            for (var i = 0; i < managersNeeded; i++)
            {
                var userIndex = random.Next(0, users.Count - 1);
                managers.Add(users[userIndex]);
                users.RemoveAt(userIndex);
            }

            // we've now got a list of managers, and we've got a list of users who are not managers, and can become non-manager subordinates
            var managerTree = new BinaryTree(managers);
            managerTreePrepStopwatch.Stop();
            var managerTreeNodeCount = 0;
            RecursivelyCountBinaryTreeNodes(managerTree, ref managerTreeNodeCount);
            Log.Verbose($"ExecuteTemplateAsync: Manager tree node count: {managerTreeNodeCount:N0}");
            Log.Verbose($"ExecuteTemplateAsync: Manager tree prep took: {managerTreePrepStopwatch.Elapsed}");

            // navigate the binary tree and assign manager attributes
            var assignManagersStopwatch = Stopwatch.StartNew();
            RecursivelyAssignUserManagers(managerTree, templateManagerAttribute.MetaverseAttribute, users);

            // do the same for non-manager subordinates, i.e. assign everyone else a manager
            var subordinatesAssigned = 0;
            var subordinatesToAssign = users.Count / (managerTreeNodeCount - 1);
            RecursivelyAssignSubordinates(managerTree, subordinatesToAssign, users, isFirstNode: true, templateManagerAttribute.MetaverseAttribute, ref subordinatesAssigned);
            Log.Verbose($"ExecuteTemplateAsync: Assigned {subordinatesAssigned:N0} subordinates a manager");

            managerTree = null;
            assignManagersStopwatch.Stop();
            Log.Verbose($"ExecuteTemplateAsync: Assigning managers to binary tree took: {assignManagersStopwatch.Elapsed}");
        }

        private static void RemoveUnecessaryAttributeValues(DataGenerationTemplate dataGenerationTemplate, List<MetaverseObject> metaverseObjects, Random random)
        {
            var stopwatch = Stopwatch.StartNew();
            var attributeValuesRemoved = 0;
            foreach (var dataGenerationObjectType in dataGenerationTemplate.ObjectTypes)
            {
                // find all data generation template attributes that have a population percentage less than 100%
                // that we need to reduce the number of assignments down for

                var metaverseObjectsOfType = metaverseObjects.Where(m => m.Type == dataGenerationObjectType.MetaverseObjectType).ToList();
                foreach (var dataGenAttributeToReduce in dataGenerationObjectType.TemplateAttributes.Where(q => q.PopulatedValuesPercentage < 100))
                {
                    // determine how many attributes we have
                    // determine how many we need to eliminate
                    // randomly clear that many from the metaverse objects

                    var needToRemove = metaverseObjectsOfType.Count / 100 * dataGenAttributeToReduce.PopulatedValuesPercentage;
                    for (int i = 0; i < needToRemove; i++)
                    {
                        var indexToRemove = random.Next(0, metaverseObjectsOfType.Count);
                        metaverseObjectsOfType[indexToRemove].AttributeValues.RemoveAll(q => q.Attribute == dataGenAttributeToReduce.MetaverseAttribute);
                        attributeValuesRemoved++;
                    }
                }
            }
            stopwatch.Stop();
            Log.Verbose($"RemoveUnecessaryAttributeValues: Removed {attributeValuesRemoved.ToString("N0")} attribute values. Took {stopwatch.Elapsed} to complete");
        }
        #endregion

        #region Raw Value Generation
        private static string ReplaceAttributeVariables(MetaverseObject metaverseObject, string textToProcess)
        {
            // match attribute variables
            // enumerate, find their value and replace
            var regex = new Regex("({.*?})", RegexOptions.Compiled);
            foreach (Match match in regex.Matches(textToProcess))
            {
                // snip off the brackets: {} to get the attribute name, i.e FirstName
                var attributeName = match.Value[1..^1];

                // find the attribute value on the Metaverse Object:
                var attribute = metaverseObject.AttributeValues.SingleOrDefault(q => q.Attribute.Name == attributeName);
                if (attribute == null)
                    throw new InvalidDataException($"AttributeValue not found for Attribute: {attributeName}. Check your pattern. Check that you have added the DataGenerationTemplateAttribute before the pattern is defined.");

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
                    DataGenerationValueTracker? uniqueIntTracker = null;
                    lock (dataGenerationValueTrackers)
                        uniqueIntTracker = dataGenerationValueTrackers.SingleOrDefault(q => q.ObjectTypeId == metaverseObject.Type.Id && q.AttributeId == metaverseAttribute.Id && q.StringValue == textWithoutSystemVar);

                    if (uniqueIntTracker == null)
                    {
                        // this is a unique value, not previously assigned. it does not need a unique int added.
                        textToProcess = textWithoutSystemVar;

                        // add it to the tracker
                        lock (dataGenerationValueTrackers)
                            dataGenerationValueTrackers.Add(new DataGenerationValueTracker { ObjectTypeId = metaverseObject.Type.Id, AttributeId = metaverseAttribute.Id, StringValue = textWithoutSystemVar, LastIntAssigned = 1 });
                    }
                    else
                    {
                        // this is not a unique value, we've generated it before. we need a unique int added.
                        // increase the tracker last int assigned value by one as well for next time we generate the same value again
                        lock (uniqueIntTracker)
                            uniqueIntTracker.LastIntAssigned += 1;

                        textToProcess = textToProcess.Replace(match.Value, uniqueIntTracker.LastIntAssigned.ToString());
                    }
                }
            }

            return textToProcess;
        }

        private static int GenerateNumberValue(MetaverseObjectType metaverseObjectType, DataGenerationTemplateAttribute dataGenTemplateAttribute, Random random, List<DataGenerationValueTracker> trackers)
        {
            int value = 1;
            int attributeId;
            if (dataGenTemplateAttribute.MetaverseAttribute != null)
                attributeId = dataGenTemplateAttribute.MetaverseAttribute.Id;
            else
                throw new InvalidDataException("Only supporting MetaverseObjects for now");

            if (dataGenTemplateAttribute.RandomNumbers.HasValue && dataGenTemplateAttribute.RandomNumbers.Value)
            {
                // random numbers
                if (dataGenTemplateAttribute.MinNumber.HasValue && !dataGenTemplateAttribute.MaxNumber.HasValue)
                {
                    // min value only
                    value = random.Next(dataGenTemplateAttribute.MinNumber.Value, int.MaxValue);
                }
                else if (!dataGenTemplateAttribute.MinNumber.HasValue && dataGenTemplateAttribute.MaxNumber.HasValue)
                {
                    // max value only
                    value = random.Next(dataGenTemplateAttribute.MaxNumber.Value);
                }
                else if (dataGenTemplateAttribute.MinNumber.HasValue && dataGenTemplateAttribute.MaxNumber.HasValue)
                {
                    // min and max values
                    value = random.Next(dataGenTemplateAttribute.MinNumber.Value, dataGenTemplateAttribute.MaxNumber.Value);
                }
            }
            else
            {
                lock (trackers)
                {
                    // sequential numbers
                    // query last value used for this object type and attribute. totally inefficient, but let's see what the performance is like first
                    var tracker = trackers.SingleOrDefault(t => t.ObjectTypeId == metaverseObjectType.Id && dataGenTemplateAttribute.MetaverseAttribute != null && t.AttributeId == dataGenTemplateAttribute.MetaverseAttribute.Id);
                    if (tracker != null && tracker.LastIntAssigned.HasValue)
                    {
                        // we've assigned a value for this attribute already. increment the value and use it
                        tracker.LastIntAssigned += 1;
                        value = tracker.LastIntAssigned.Value;
                    }
                    else
                    {
                        // we've not assigned a value to this attribute yet
                        if (dataGenTemplateAttribute.MinNumber.HasValue)
                            value = dataGenTemplateAttribute.MinNumber.Value;

                        trackers.Add(new DataGenerationValueTracker
                        {
                            ObjectTypeId = metaverseObjectType.Id,
                            AttributeId = attributeId,
                            LastIntAssigned = value
                        });
                    }
                }
            }

            return value;
        }
        #endregion

        #region Manager Assignment
        private void RecursivelyCountBinaryTreeNodes(BinaryTree binaryTree, ref int nodeCount)
        {
            nodeCount++;
            if (binaryTree.Left != null)
                RecursivelyCountBinaryTreeNodes(binaryTree.Left, ref nodeCount);

            if (binaryTree.Right != null)
                RecursivelyCountBinaryTreeNodes(binaryTree.Right, ref nodeCount);
        }

        private void RecursivelyAssignUserManagers(BinaryTree binaryTree, MetaverseAttribute managerAttribute, List<MetaverseObject> subordinates)
        {
            // binaryTree.MetaverseObject is the manager
            // assign this in a manager attribute to both the left and right branches

            if (binaryTree.Left != null)
            {
                binaryTree.Left.MetaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Attribute = managerAttribute,
                    ReferenceValue = binaryTree.MetaverseObject
                });

                RecursivelyAssignUserManagers(binaryTree.Left, managerAttribute, subordinates);
            }

            if (binaryTree.Right != null)
            {
                binaryTree.Right.MetaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Attribute = managerAttribute,
                    ReferenceValue = binaryTree.MetaverseObject
                });

                RecursivelyAssignUserManagers(binaryTree.Right, managerAttribute, subordinates);
            }
        }
    
        private void RecursivelyAssignSubordinates(BinaryTree binaryTree, int subordinatesToAssign, List<MetaverseObject> users, bool isFirstNode, MetaverseAttribute managerAttribute, ref int subordinatesAssigned)
        {
            if (isFirstNode)
            {
                // don't assign any subordinates. the top manager can just have managers as subordinates
                isFirstNode = false;
            }
            else
            {
                // take the required number of subordinates out of the user list and assign them as subordinates to this manager
                var availableSubordinates = users.Count >= subordinatesToAssign ? subordinatesToAssign : users.Count;
                var subordinates = new MetaverseObject[availableSubordinates];
                for (var i = 0; i < availableSubordinates; i++)
                    subordinates[i] = users[i];
                users.RemoveRange(0, subordinates.Length - 1);

                foreach (var user in subordinates)
                {
                    user.AttributeValues.Add(new MetaverseObjectAttributeValue
                    {
                        Attribute = managerAttribute,
                        ReferenceValue = binaryTree.MetaverseObject
                    });

                    subordinatesAssigned++;
                }
            }

            // now recurse and do the same for the left and right branches, if they exist
            if (binaryTree.Left != null)
                RecursivelyAssignSubordinates(binaryTree.Left, subordinatesToAssign, users, isFirstNode, managerAttribute, ref subordinatesAssigned);

            if (binaryTree.Right != null)
                RecursivelyAssignSubordinates(binaryTree.Right, subordinatesToAssign, users, isFirstNode, managerAttribute, ref subordinatesAssigned);
        }
        #endregion
    }
}