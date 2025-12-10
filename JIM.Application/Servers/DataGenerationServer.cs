using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.DataGeneration.DTOs;
using JIM.Models.Utility;
using Serilog;
using System.Diagnostics;
using System.Text.RegularExpressions;
namespace JIM.Application.Servers;

public class DataGenerationServer
{
    #region accessors
    private JimApplication Application { get; }
    #endregion

    #region members
    private readonly object _valuesLock = new();
    private readonly object _metaverseObjectLock = new();
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

    public async Task<List<ExampleDataSetHeader>> GetExampleDataSetHeadersAsync()
    {
        return await Application.Repository.DataGeneration.GetExampleDataSetHeadersAsync();
    }

    public async Task<ExampleDataSet?> GetExampleDataSetAsync(string name, string culture)
    {
        return await Application.Repository.DataGeneration.GetExampleDataSetAsync(name, culture);
    }

    public async Task<ExampleDataSet?> GetExampleDataSetAsync(int id)
    {
        return await Application.Repository.DataGeneration.GetExampleDataSetAsync(id);
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

    public async Task<List<DataGenerationTemplateHeader>> GetTemplateHeadersAsync()
    {
        return await Application.Repository.DataGeneration.GetTemplateHeadersAsync();
    }

    public async Task<DataGenerationTemplate?> GetTemplateAsync(int id)
    {
        return await Application.Repository.DataGeneration.GetTemplateAsync(id);
    }

    public async Task<DataGenerationTemplate?> GetTemplateAsync(string name)
    {
        return await Application.Repository.DataGeneration.GetTemplateAsync(name);
    }

    public async Task<DataGenerationTemplateHeader?> GetTemplateHeaderAsync(int id)
    {
        return await Application.Repository.DataGeneration.GetTemplateHeaderAsync(id);
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

    public async Task ExecuteTemplateAsync(int templateId, CancellationToken cancellationToken)
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
        var template = await GetTemplateAsync(templateId);
        getTemplateStopwatch.Stop();
        Log.Verbose($"ExecuteTemplateAsync: get template took: {getTemplateStopwatch.Elapsed}");

        if (template == null)
            throw new ArgumentException("No template found with that id");

        template.Validate();

        // object type dependency graph needs considering
        // for now we should probably just advise people to add template object types in reverse order to how they're referenced.
        // note: entity framework might handle dependency sequencing for us at time of persistence

        var random = new Random();
        var metaverseObjectsToCreate = new List<MetaverseObject>();
        var dataGenerationValueTrackers = new List<DataGenerationValueTracker>();
            
        // we've had issues with EF not returning values for example datasets when retrieving the template
        // so we're going to get all the example datasets referenced in a template separately and passing them in as needed.
        var exampleDataSets = new List<ExampleDataSet>();
        foreach (var datasetInstance in from objectType in template.ObjectTypes from templateAttribute in objectType.TemplateAttributes from datasetInstance in templateAttribute.ExampleDataSetInstances select datasetInstance)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Log.Debug("ExecuteTemplateAsync: Cancellation requested. Returning from data set processing prematurely.");
                return;
            }

            if (datasetInstance?.ExampleDataSet == null || exampleDataSets.Any(q => q.Id == datasetInstance.ExampleDataSet.Id)) 
                continue;
                
            var exampleDataSet = await GetExampleDataSetAsync(datasetInstance.ExampleDataSet.Id);
            if (exampleDataSet != null)
                exampleDataSets.Add(exampleDataSet);
        }

        foreach (var objectType in template.ObjectTypes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Log.Debug("ExecuteTemplateAsync: Cancellation requested. Returning from object type processing prematurely.");
                return;
            }

            var objectTypeStopWatch = Stopwatch.StartNew();
            Log.Verbose($"ExecuteTemplateAsync: Processing metaverse object type: {objectType.MetaverseObjectType.Name}");
            var trackers = dataGenerationValueTrackers;
            var create = metaverseObjectsToCreate;
            Parallel.For(0, objectType.ObjectsToCreate,
                index =>
                {
                    var metaverseObject = new MetaverseObject { Type = objectType.MetaverseObjectType };
                    // make sure we process attributes with no dependencies first
                    foreach (var templateAttribute in objectType.TemplateAttributes.OrderBy(q => q.AttributeDependency))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            Log.Verbose("ExecuteTemplateAsync: Cancellation requested. Returning from attribute processing prematurely.");
                            return;
                        }

                        // only supporting Metaverse attributes for now.
                        // generating values for Connector Space values will have to come later, subject to demand
                        if (templateAttribute.MetaverseAttribute != null)
                        {
                            // is this attribute dependent upon another?
                            if (templateAttribute.AttributeDependency != null)
                            {
                                // get the dependent attribute value
                                var dependentAttributeValue = metaverseObject.AttributeValues.SingleOrDefault(q => q.Attribute.Id == templateAttribute.AttributeDependency.MetaverseAttribute.Id);
                                if (dependentAttributeValue == null)
                                {
                                    // there's no dependent attribute, so nothing to compare. do not generate a value
                                    continue;
                                }

                                if (templateAttribute.AttributeDependency.ComparisonType == ComparisonType.Equals)
                                {
                                    if (dependentAttributeValue.StringValue != templateAttribute.AttributeDependency.StringValue)
                                    {
                                        Log.Debug($"ExecuteTemplateAsync: Not generating {templateAttribute.MetaverseAttribute.Name} attribute value, as dependent attribute value '{dependentAttributeValue.StringValue}' does not equal '{templateAttribute.AttributeDependency.StringValue}'");
                                        continue;
                                    }
                                }
                                else
                                {
                                    throw new NotSupportedException("Not currently supporting ComparisonTypes other than Equals");
                                }
                            }

                            // handle each attribute type in dedicated functions
                            switch (templateAttribute.MetaverseAttribute.Type)
                            {
                                case AttributeDataType.Text:
                                    GenerateMetaverseStringValue(metaverseObject, templateAttribute, exampleDataSets, random, trackers);
                                    break;
                                case AttributeDataType.Guid:
                                    GenerateMetaverseGuidValue(metaverseObject, templateAttribute);
                                    break;
                                case AttributeDataType.Number:
                                    GenerateMetaverseNumberValue(metaverseObject, templateAttribute, random, trackers);
                                    break;
                                case AttributeDataType.DateTime:
                                    GenerateMetaverseDateTimeValue(metaverseObject, templateAttribute, random);
                                    break;
                                case AttributeDataType.Boolean:
                                    GenerateMetaverseBooleanValue(metaverseObject, templateAttribute, random);
                                    break;
                                case AttributeDataType.Reference:
                                    GenerateMetaverseReferenceValue(metaverseObject, templateAttribute, random, create);
                                    break;
                                case AttributeDataType.NotSet:
                                    break;
                                case AttributeDataType.Binary:
                                    break;
                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                        }
                    }

                    lock (_metaverseObjectLock)
                        create.Add(metaverseObject);

                    Interlocked.Add(ref totalObjectsCreated, 1);
                });

            // user manager attributes need assigning after all users have been prepared
            GenerateManagerAssignments(metaverseObjectsToCreate, objectType, random);

            objectTypeStopWatch.Stop();
            Log.Information($"ExecuteTemplateAsync: It took {objectTypeStopWatch.Elapsed} to process the {objectType.MetaverseObjectType.Name} metaverse object type");
        }

        // ensure that attribute population percentage values are respected
        // do this by assigning all attributes with values (done), then go and randomly delete the required amount
        RemoveUnnecessaryAttributeValues(template, metaverseObjectsToCreate, random);
        Log.Information($"ExecuteTemplateAsync: Generated {metaverseObjectsToCreate.Count:N0} objects");
        objectPreparationStopwatch.Stop();

        if (cancellationToken.IsCancellationRequested)
        {
            Log.Debug("ExecuteTemplateAsync: Cancellation requested. Returning after removing unecessary attributes prematurely.");
            return;
        }

        // submit metaverse objects to data layer for creation
        var persistenceStopwatch = new Stopwatch();
        persistenceStopwatch.Start();

        try
        {
            await Application.Repository.DataGeneration.CreateMetaverseObjectsAsync(metaverseObjectsToCreate, cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            Log.Information(ex, "ExecuteTemplateAsync: Template '{template.Name}' object persistence did not complete as cancellation was requested.");
        }

        persistenceStopwatch.Stop();
        totalTimeStopwatch.Stop();
        Log.Information($"ExecuteTemplateAsync: Template '{template.Name}' complete. {totalObjectsCreated:N0} objects prepared in {objectPreparationStopwatch.Elapsed}. Persisted in {persistenceStopwatch.Elapsed}. Total time: {totalTimeStopwatch.Elapsed}");

        // trying to help garbage collection along. data generation results in a lot of ram usage.
        metaverseObjectsToCreate.Clear();
        dataGenerationValueTrackers.Clear();
    }
    #endregion

    #region Attribute Generation
    private void GenerateMetaverseStringValue(
        MetaverseObject metaverseObject,
        DataGenerationTemplateAttribute dataGenerationTemplateAttribute,
        IEnumerable<ExampleDataSet> exampleDataSets,
        Random random,
        List<DataGenerationValueTracker> dataGenerationValueTrackers)
    {
        if (dataGenerationTemplateAttribute.MetaverseAttribute == null)
            throw new ArgumentNullException(nameof(dataGenerationTemplateAttribute));

        // a string attribute can have a string type or number type value assigned
        if (dataGenerationTemplateAttribute.IsUsingStrings())
        {
            // logic:
            // - if no pattern: handle one or more data set value assignments
            // - if pattern: replace attribute vars, replace system vars and replace example data set vars

            string output;
            if (string.IsNullOrEmpty(dataGenerationTemplateAttribute.Pattern) && dataGenerationTemplateAttribute.ExampleDataSetInstances.Count == 1)
            {
                // for some reason, this sometimes loads with zero values and an exception is thrown
                // no idea why. need to spend time trying to diagnose this. For now, skip the scenario.
                if (dataGenerationTemplateAttribute.ExampleDataSetInstances[0].ExampleDataSet.Values.Count == 0)
                {
                    //Log.Error("GenerateMetaverseStringValue: dataGenerationTemplateAttribute.ExampleDataSetInstances[0].ExampleDataSet.Values.Count was zero!");
                    //return;

                    dataGenerationTemplateAttribute.ExampleDataSetInstances[0].ExampleDataSet = exampleDataSets.Single(q => q.Id == dataGenerationTemplateAttribute.ExampleDataSetInstances[0].ExampleDataSet.Id);
                }

                // single example-data set based
                var valueIndex = random.Next(0, dataGenerationTemplateAttribute.ExampleDataSetInstances[0].ExampleDataSet.Values.Count);
                output = dataGenerationTemplateAttribute.ExampleDataSetInstances[0].ExampleDataSet.Values[valueIndex].StringValue;
            }
            else if (string.IsNullOrEmpty(dataGenerationTemplateAttribute.Pattern) && dataGenerationTemplateAttribute.ExampleDataSetInstances.Count > 1)
            {
                // multiple example-data set based:
                // just choose randomly a value from across the datasets. simplest for now
                // would prefer to end up with an even distribution of values from across the datasets, but I ran out of time.                 
                var dataSetIndex = random.Next(0, dataGenerationTemplateAttribute.ExampleDataSetInstances.Count);

                // for some reason, Firstnames Female sometimes loads with zero values and an exception is thrown
                // no idea why. need to spend time trying to diagnose this. For now, skip the scenario.
                if (dataGenerationTemplateAttribute.ExampleDataSetInstances[dataSetIndex].ExampleDataSet.Values.Count == 0)
                {
                    //Log.Error("GenerateMetaverseStringValue: dataGenerationTemplateAttribute.ExampleDataSetInstances[dataSetIndex].ExampleDataSet.Values.Count was zero!");
                    //return;

                    dataGenerationTemplateAttribute.ExampleDataSetInstances[dataSetIndex].ExampleDataSet = exampleDataSets.Single(q => q.Id == dataGenerationTemplateAttribute.ExampleDataSetInstances[dataSetIndex].ExampleDataSet.Id);
                }

                var valueIndexMaxValue = dataGenerationTemplateAttribute.ExampleDataSetInstances[dataSetIndex].ExampleDataSet.Values.Count;
                var valueIndex = random.Next(0, valueIndexMaxValue);

                output = dataGenerationTemplateAttribute.ExampleDataSetInstances[dataSetIndex].ExampleDataSet.Values[valueIndex].StringValue;
            }
            else if (!string.IsNullOrEmpty(dataGenerationTemplateAttribute.Pattern))
            {
                // pattern generation:
                // parse out the attribute variables {var} and system variables [var]
                // use regex to do this. keep it simple for now, just replace what you find
                // later on we can look at encapsulation, i.e. functions around vars, and functions around functions.
                // replace attribute vars first, then check system vars, i.e. uniqueness ids against complete generated string.
                output = ReplaceAttributeVariables(metaverseObject, dataGenerationTemplateAttribute.Pattern);
                output = ReplaceSystemVariables(metaverseObject, dataGenerationTemplateAttribute.MetaverseAttribute, dataGenerationValueTrackers, output);
                output = ReplaceExampleDataSetVariables(metaverseObject, dataGenerationTemplateAttribute.MetaverseAttribute, dataGenerationTemplateAttribute.ExampleDataSetInstances, dataGenerationValueTrackers, random, output);
            }
            else if (dataGenerationTemplateAttribute.WeightedStringValues is { Count: > 0 })
            {
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                output = dataGenerationTemplateAttribute.WeightedStringValues.RandomElementByWeight(x => x.Weight).Value;
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            }
            else
            {
                throw new InvalidDataException("DataGenerationTemplateAttribute string attribute configuration not as expected");
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

    private void GenerateMetaverseNumberValue(
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

        var startDate = DateTime.MinValue;
        var endDate = DateTime.MaxValue;
        if (dataGenerationTemplateAttribute is { MinDate: not null, MaxDate: not null })
        {
            // between two dates
            startDate = dataGenerationTemplateAttribute.MinDate.Value;
            endDate = dataGenerationTemplateAttribute.MaxDate.Value;
        }
        else if (dataGenerationTemplateAttribute is { MinDate: not null, MaxDate: null })
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
        var date = startDate + newSpan;

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
        // TODO: implement true value distribution logic
        //}
        //else
        //{
        // bool should be random
        value = Convert.ToBoolean(random.Next(0, 2));
        //}

        metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = dataGenerationTemplateAttribute.MetaverseAttribute,
            BoolValue = value
        });
    }

    private static void GenerateMetaverseReferenceValue(MetaverseObject metaverseObject, DataGenerationTemplateAttribute templateAttribute, Random random, List<MetaverseObject> metaverseObjects)
    {
        if (templateAttribute.MetaverseAttribute == null)
            return;

        // skip if this is for a user manager attribute, that's specially handled elsewhere
        if (metaverseObject.Type.Name == Constants.BuiltInObjectTypes.User && templateAttribute.MetaverseAttribute.Name == Constants.BuiltInAttributes.Manager)
            return;


        // debug point. q was null in the query below for some reason. haven't been able to catch it yet
        if (metaverseObjects == null)
        {
            return;
        }

        // is this going to be slow?
        var metaverseObjectsOfTypes = metaverseObjects.Where(q => q != null &&
                                                                  templateAttribute.ReferenceMetaverseObjectTypes != null &&
                                                                  templateAttribute.ReferenceMetaverseObjectTypes.Contains(q.Type)).ToList();

        if (metaverseObjectsOfTypes.Count == 0)
            return;

        if (templateAttribute.MetaverseAttribute.AttributePlurality == AttributePlurality.SingleValued)
        {
            // pick a random metaverse object and assign
            var referencedMetaverseObjectIndex = random.Next(0, metaverseObjectsOfTypes.Count);
            var referencedMetaverseObject = metaverseObjectsOfTypes[referencedMetaverseObjectIndex];
            metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Attribute = templateAttribute.MetaverseAttribute,
                ReferenceValue = referencedMetaverseObject
            });
        }
        else
        {
            // multi-valued attribute
            // determine how many values to pick
            var min = templateAttribute.MvaRefMinAssignments ?? 0;
            var max = templateAttribute.MvaRefMaxAssignments ?? metaverseObjectsOfTypes.Count;
            var attributeValuesToCreate = random.Next(min, max);

            for (var i = 0; i < attributeValuesToCreate; i++)
            {
                var referencedObject = metaverseObjectsOfTypes[random.Next(0, metaverseObjectsOfTypes.Count)];
                metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Attribute = templateAttribute.MetaverseAttribute,
                    ReferenceValue = referencedObject
                });
            }
        }
    }

    private void GenerateManagerAssignments(List<MetaverseObject> metaverseObjectsToCreate, DataGenerationObjectType objectType, Random random)
    {
        Log.Verbose("GenerateManagerAssignments: Started...");
        var templateManagerAttribute = objectType.TemplateAttributes.SingleOrDefault(ta =>
            ta.MetaverseAttribute != null &&
            ta.MetaverseAttribute.Name == Constants.BuiltInAttributes.Manager);

        if (objectType.MetaverseObjectType.Name != Constants.BuiltInObjectTypes.User || templateManagerAttribute is not { MetaverseAttribute: not null, ManagerDepthPercentage: not null })
            return;
            
        // binary tree approach:
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
        var managersNeeded = users.Count * templateManagerAttribute.ManagerDepthPercentage.Value / 100;

        // randomly select managers and remove them from the users list so we have a list of managers and a list of potential direct reports
        var managers = new List<MetaverseObject>();
        for (var i = 0; i < managersNeeded; i++)
        {
            var userIndex = random.Next(0, users.Count);
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
        RecursivelyAssignUserManagers(managerTree, templateManagerAttribute.MetaverseAttribute);

        // do the same for non-manager subordinates, i.e. assign everyone else a manager
        var subordinatesAssigned = 0;
        var subordinatesToAssign = managerTreeNodeCount > 1 ? users.Count / (managerTreeNodeCount - 1) : users.Count;
        RecursivelyAssignSubordinates(managerTree, subordinatesToAssign, users, isFirstNode: true, templateManagerAttribute.MetaverseAttribute, ref subordinatesAssigned);
        Log.Verbose($"ExecuteTemplateAsync: Assigned {subordinatesAssigned:N0} subordinates a manager");

        managerTree = null;
        assignManagersStopwatch.Stop();
        Log.Verbose($"ExecuteTemplateAsync: Assigning managers to binary tree took: {assignManagersStopwatch.Elapsed}");
    }

    private static void RemoveUnnecessaryAttributeValues(DataGenerationTemplate dataGenerationTemplate, IReadOnlyCollection<MetaverseObject> metaverseObjects, Random random)
    {
        Log.Verbose("RemoveUnnecessaryAttributeValues: Started...");
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

                var percentage = dataGenAttributeToReduce.PopulatedValuesPercentage ?? 100;
                var needToRemove = metaverseObjectsOfType.Count * (100 - percentage) / 100;
                for (var i = 0; i < needToRemove; i++)
                {
                    var indexToRemove = random.Next(0, metaverseObjectsOfType.Count);
                    metaverseObjectsOfType[indexToRemove].AttributeValues.RemoveAll(q => q.Attribute == dataGenAttributeToReduce.MetaverseAttribute);
                    attributeValuesRemoved++;
                }
            }
        }
        stopwatch.Stop();
        Log.Verbose($"RemoveUnnecessaryAttributeValues: Removed {attributeValuesRemoved:N0} attribute values. Took {stopwatch.Elapsed} to complete");
    }
    #endregion

    #region Attribute Value Generation
    private static string ReplaceAttributeVariables(MetaverseObject metaverseObject, string textToProcess)
    {
        // match attribute variables (that do not contain numbers)
        // enumerate, find their value and replace
        var regex = new Regex(@"({.*?[^\d]})", RegexOptions.Compiled);
        foreach (var match in regex.Matches(textToProcess).Cast<Match>())
        {
            // snip off the brackets: {} to get the attribute name, i.e FirstName
            var attributeName = match.Value[1..^1];

            // find the attribute value on the Metaverse Object:
            var attribute = metaverseObject.AttributeValues.SingleOrDefault(q => q.Attribute.Name == attributeName) ?? throw new InvalidDataException($"AttributeValue not found for Attribute: {attributeName}. Check your pattern. Check that you have added the DataGenerationTemplateAttribute before the pattern is defined.");
            textToProcess = textToProcess.Replace(match.Value, attribute.StringValue);
        }

        return textToProcess;
    }

    private string ReplaceSystemVariables(
        MetaverseObject metaverseObject,
        MetaverseAttribute metaverseAttribute,
        List<DataGenerationValueTracker> dataGenerationValueTrackers,
        string textToProcess)
    {
        // match system variables
        // enumerate, process
        var regex = new Regex(@"(\[.*?\])", RegexOptions.Compiled);
        var systemVars = regex.Matches(textToProcess);
        foreach (var match in systemVars.Cast<Match>())
        {
            // snip off the brackets: {} to get the attribute name, i.e FirstName
            var variableName = match.Value[1..^1];

            // keeping these as strings for now. They will need evolving into part of the Functions feature at some point
            if (variableName != "UniqueInt") 
                continue;
                
            // is the string value unique amongst all MetaverseObjects of the same type?
            // if so, replace the system variable with an empty string
            // if not, add a uniqueness in in place of the system variable

            // get the text value without any unique int added, i.e. "joe.bloggs@demo.tetron.io"
            var textWithoutSystemVar = textToProcess.Replace(match.Value, string.Empty);
                
            lock (_valuesLock)
            {
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

    private string ReplaceExampleDataSetVariables(
        MetaverseObject metaverseObject,
        MetaverseAttribute metaverseAttribute,
        List<ExampleDataSetInstance> exampleDataSetInstances,
        List<DataGenerationValueTracker> dataGenerationValueTrackers,
        Random random,
        string textToProcess)
    {
        // logic:
        // - replace each example data set variable in the pattern with a random value from example data sets, populating a new value variable
        // - check if the new value variable value is unique via the tracked values list
        // - if not, re-run until it is unique

        // match example data set variables i.e. {0}
        // enumerate, process

        if (exampleDataSetInstances == null || exampleDataSetInstances.Count == 0)
            return textToProcess;

        var regex = new Regex(@"({\d.*?})", RegexOptions.Compiled);
        var exampleDataSetVariables = regex.Matches(textToProcess);

        if (exampleDataSetVariables.Count == 0)
            return textToProcess;

        var isGeneratedValueUnique = false;
        while (!isGeneratedValueUnique)
        {
            var completeGeneratedValue = textToProcess;
            foreach (Match match in exampleDataSetVariables.Cast<Match>())
            {
                // snip off the brackets: {} to get the variable, then test if it's an ExampleDataSet index, i.e. {0}
                var variable = match.Value[1..^1];
                var exampleDataSetIndex = int.Parse(variable);

                if (exampleDataSetIndex >= exampleDataSetInstances.Count)
                    throw new InvalidDataException("DataGenerationTemplateAttribute example data set index variable is too high. Smaller number needed. Must be within the bounds of the assigned ExampleDataSets");

                // get the example data set and then choose a random value from it before replacing the variable
                var exampleDataSet = exampleDataSetInstances[exampleDataSetIndex].ExampleDataSet;
                var randomValueIndex = random.Next(0, exampleDataSet.Values.Count);
                var randomValue = exampleDataSet.Values[randomValueIndex].StringValue;

                if (string.IsNullOrEmpty(randomValue))
                    throw new InvalidDataException("Did not get a string ExampleDataSetValue value from the randomly selected list of values.");

                // replace the example data set variable with the random value
                completeGeneratedValue = completeGeneratedValue.Replace(match.Value, randomValue);
            }

            lock (_valuesLock)
            {
                // is the generated value unique? exit if so
                var uniqueStringTracker = dataGenerationValueTrackers.SingleOrDefault(q => q.ObjectTypeId == metaverseObject.Type.Id && q.AttributeId == metaverseAttribute.Id && q.StringValue == completeGeneratedValue);
                if (uniqueStringTracker == null)
                {
                    // generated value is unique
                    isGeneratedValueUnique = true;
                    textToProcess = completeGeneratedValue;

                    // add the generated value to the tracker so we don't end up generating and assigning it again
                    dataGenerationValueTrackers.Add(new DataGenerationValueTracker { ObjectTypeId = metaverseObject.Type.Id, AttributeId = metaverseAttribute.Id, StringValue = textToProcess });
                }
                else
                {
                    // this is not a unique value, we've generated it before. go round again until it is unique
                    //Log.Verbose($"ReplaceExampleDataSetVariables: Duplicate generated value detected. Skipping: {completeGeneratedValue}");
                }
            }
        }

        return textToProcess;
    }

    private int GenerateNumberValue(MetaverseObjectType metaverseObjectType, DataGenerationTemplateAttribute dataGenTemplateAttribute, Random random, ICollection<DataGenerationValueTracker> trackers)
    {
        var value = 1;
        int attributeId;
        if (dataGenTemplateAttribute.MetaverseAttribute != null)
            attributeId = dataGenTemplateAttribute.MetaverseAttribute.Id;
        else
            throw new InvalidDataException("Only supporting MetaverseObjects for now");

        if (dataGenTemplateAttribute.RandomNumbers.HasValue && dataGenTemplateAttribute.RandomNumbers.Value)
        {
            // random numbers
            if (dataGenTemplateAttribute is { MinNumber: not null, MaxNumber: null })
            {
                // min value only
                value = random.Next(dataGenTemplateAttribute.MinNumber.Value, int.MaxValue);
            }
            else if (!dataGenTemplateAttribute.MinNumber.HasValue && dataGenTemplateAttribute.MaxNumber.HasValue)
            {
                // max value only
                value = random.Next(dataGenTemplateAttribute.MaxNumber.Value);
            }
            else if (dataGenTemplateAttribute is { MinNumber: not null, MaxNumber: not null })
            {
                // min and max values
                value = random.Next(dataGenTemplateAttribute.MinNumber.Value, dataGenTemplateAttribute.MaxNumber.Value);
            }
        }
        else
        {
            lock (_valuesLock)
            {
                // sequential numbers
                // query last value used for this object type and attribute. totally inefficient, but let's see what the performance is like first
                var tracker = trackers.SingleOrDefault(t => t.ObjectTypeId == metaverseObjectType.Id && dataGenTemplateAttribute.MetaverseAttribute != null && t.AttributeId == dataGenTemplateAttribute.MetaverseAttribute.Id);
                if (tracker is { LastIntAssigned: not null })
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
    private static void RecursivelyCountBinaryTreeNodes(BinaryTree binaryTree, ref int nodeCount)
    {
        nodeCount++;
        if (binaryTree.Left != null)
            RecursivelyCountBinaryTreeNodes(binaryTree.Left, ref nodeCount);

        if (binaryTree.Right != null)
            RecursivelyCountBinaryTreeNodes(binaryTree.Right, ref nodeCount);
    }

    private static void RecursivelyAssignUserManagers(BinaryTree binaryTree, MetaverseAttribute managerAttribute)
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

            RecursivelyAssignUserManagers(binaryTree.Left, managerAttribute);
        }

        if (binaryTree.Right == null) 
            return;
            
        binaryTree.Right.MetaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = managerAttribute,
            ReferenceValue = binaryTree.MetaverseObject
        });

        RecursivelyAssignUserManagers(binaryTree.Right, managerAttribute);
    }

    private static void RecursivelyAssignSubordinates(BinaryTree binaryTree, int subordinatesToAssign, List<MetaverseObject> users, bool isFirstNode, MetaverseAttribute managerAttribute, ref int subordinatesAssigned)
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
            users.RemoveRange(0, subordinates.Length);

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