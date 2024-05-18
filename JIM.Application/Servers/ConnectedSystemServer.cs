using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Utility;
using Serilog;

namespace JIM.Application.Servers;

public class ConnectedSystemServer
{
    private JimApplication Application { get; }

    internal ConnectedSystemServer(JimApplication application)
    {
        Application = application;
    }

    #region Connector Definitions
    public async Task<IList<ConnectorDefinitionHeader>> GetConnectorDefinitionHeadersAsync()
    {
        return await Application.Repository.ConnectedSystems.GetConnectorDefinitionHeadersAsync();
    }

    public async Task<ConnectorDefinition?> GetConnectorDefinitionAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetConnectorDefinitionAsync(id);
    }

    public async Task<ConnectorDefinition?> GetConnectorDefinitionAsync(string name)
    {
        return await Application.Repository.ConnectedSystems.GetConnectorDefinitionAsync(name);
    }

    public async Task CreateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition)
    {
        await Application.Repository.ConnectedSystems.CreateConnectorDefinitionAsync(connectorDefinition);
    }

    public async Task UpdateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition)
    {
        await Application.Repository.ConnectedSystems.UpdateConnectorDefinitionAsync(connectorDefinition);
    }

    public async Task DeleteConnectorDefinitionAsync(ConnectorDefinition connectorDefinition)
    {
        await Application.Repository.ConnectedSystems.DeleteConnectorDefinitionAsync(connectorDefinition);
    }

    public async Task CreateConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile)
    {
        await Application.Repository.ConnectedSystems.CreateConnectorDefinitionFileAsync(connectorDefinitionFile);
    }

    public async Task DeleteConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile)
    {
        await Application.Repository.ConnectedSystems.DeleteConnectorDefinitionFileAsync(connectorDefinitionFile);
    }
    #endregion

    #region Connected Systems
    public async Task<List<ConnectedSystem>> GetConnectedSystemsAsync()
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemsAsync();
    }

    public async Task<List<ConnectedSystemHeader>> GetConnectedSystemHeadersAsync()
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemHeadersAsync();
    }

    public async Task<ConnectedSystem?> GetConnectedSystemAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemAsync(id);
    }

    public async Task<ConnectedSystemHeader?> GetConnectedSystemHeaderAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemHeaderAsync(id);
    }

    public int GetConnectedSystemCount()
    {
        return Application.Repository.ConnectedSystems.GetConnectedSystemCount();
    }
        
    public async Task CreateConnectedSystemAsync(ConnectedSystem connectedSystem, MetaverseObject initiatedBy)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        if (connectedSystem.ConnectorDefinition == null)
            throw new ArgumentException("connectedSystem.ConnectorDefinition is null!");

        if (connectedSystem.ConnectorDefinition.Settings == null || connectedSystem.ConnectorDefinition.Settings.Count == 0)
            throw new ArgumentException("connectedSystem.ConnectorDefinition has no settings. Cannot construct a valid connectedSystem object!");

        if (!AreRunProfilesValid(connectedSystem))
            throw new ArgumentException("connectedSystem.RunProfiles has some of a run type that is not supported by the Connector.");

        // create the connected system setting value objects from the connected system definition settings
        foreach (var connectedSystemDefinitionSetting in connectedSystem.ConnectorDefinition.Settings)
        {
            var settingValue = new ConnectedSystemSettingValue {
                Setting = connectedSystemDefinitionSetting
            };

            if (connectedSystemDefinitionSetting is { Type: ConnectedSystemSettingType.CheckBox, DefaultCheckboxValue: not null })
                settingValue.CheckboxValue = connectedSystemDefinitionSetting.DefaultCheckboxValue.Value;

            if (connectedSystemDefinitionSetting.Type == ConnectedSystemSettingType.String && !string.IsNullOrEmpty(connectedSystemDefinitionSetting.DefaultStringValue))
                settingValue.StringValue = connectedSystemDefinitionSetting.DefaultStringValue.Trim();

            if (connectedSystemDefinitionSetting is { Type: ConnectedSystemSettingType.Integer, DefaultIntValue: not null })
                settingValue.IntValue = connectedSystemDefinitionSetting.DefaultIntValue.Value;

            connectedSystem.SettingValues.Add(settingValue);
        }

        SanitiseConnectedSystemUserInput(connectedSystem);

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Create
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        await Application.Repository.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem, MetaverseObject initiatedBy, Activity? parentActivity = null)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        if (!AreRunProfilesValid(connectedSystem))
            throw new ArgumentException("connectedSystem.RunProfiles has some of a run type that is not supported by the Connector.");

        Log.Verbose($"UpdateConnectedSystemAsync() called for {connectedSystem}");

        // are the settings valid?
        var validationResults = ValidateConnectedSystemSettings(connectedSystem);
        connectedSystem.SettingValuesValid = validationResults.All(q => q.IsValid);

        connectedSystem.LastUpdated = DateTime.UtcNow;

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Update,
            ParentActivityId = parentActivity?.Id,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
            
        SanitiseConnectedSystemUserInput(connectedSystem);
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);
            
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Try and prevent the user from supplying unusable input.
    /// </summary>
    private static void SanitiseConnectedSystemUserInput(ConnectedSystem connectedSystem)
    {
        connectedSystem.Name = connectedSystem.Name.Trim();
        if (!string.IsNullOrEmpty(connectedSystem.Description))
            connectedSystem.Description = connectedSystem.Description.Trim();

        foreach (var settingValue in connectedSystem.SettingValues)
            if (!string.IsNullOrEmpty(settingValue.StringValue))
                settingValue.StringValue = settingValue.StringValue.Trim();
    }
    #endregion

    #region Connected System Settings
    /// <summary>
    /// Use this when a connector is being parsed for persistence as a connector definition to create the connector definition settings from the connector instance.
    /// </summary>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public void CopyConnectorSettingsToConnectorDefinition(IConnectorSettings connector, ConnectorDefinition connectorDefinition)
    {
        foreach (var connectorSetting in connector.GetSettings())
        {
            connectorDefinition.Settings.Add(new ConnectorDefinitionSetting
            {
                Category = connectorSetting.Category,
                DefaultCheckboxValue = connectorSetting.DefaultCheckboxValue,
                DefaultStringValue = connectorSetting.DefaultStringValue,
                DefaultIntValue = connectorSetting.DefaultIntValue,
                Description = connectorSetting.Description,
                DropDownValues = connectorSetting.DropDownValues,
                Name = connectorSetting.Name,
                Type = connectorSetting.Type,
                Required = connectorSetting.Required
            });
        }
    }

    /// <summary>
    /// Checks that all setting values are valid, according to business rules.
    /// </summary>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public IList<ConnectorSettingValueValidationResult> ValidateConnectedSystemSettings(ConnectedSystem connectedSystem)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        // work out what connector we need to instantiate, so that we can use its internal validation method
        // 100% expecting this to be something we need to centralise/improve later as we develop the connector definition system
        // especially when we need to support uploaded connectors, not just built-in ones

        if (connectedSystem.ConnectorDefinition.Name == Connectors.ConnectorConstants.LdapConnectorName)
            return new LdapConnector().ValidateSettingValues(connectedSystem.SettingValues, Log.Logger);

        if (connectedSystem.ConnectorDefinition.Name == Connectors.ConnectorConstants.FileConnectorName)
            return new FileConnector().ValidateSettingValues(connectedSystem.SettingValues, Log.Logger);

        // todo: support custom connectors.

        throw new NotImplementedException("Support for that connector definition has not been implemented yet.");
    }

    private static void ValidateConnectedSystemParameter(ConnectedSystem connectedSystem)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        if (connectedSystem.ConnectorDefinition == null)
            throw new ArgumentException("The supplied ConnectedSystem doesn't have a valid ConnectorDefinition.", nameof(connectedSystem));

        if (connectedSystem.SettingValues == null || connectedSystem.SettingValues.Count == 0)
            throw new ArgumentException("The supplied ConnectedSystem doesn't have any valid SettingValues.", nameof(connectedSystem));
    }
    #endregion

    #region Connected System Schema
    /// <summary>
    /// Causes the associated Connector to be instantiated and the schema imported from the connected system.
    /// Changes will be persisted, even if they are destructive, i.e. an attribute is removed.
    /// </summary>
    /// <returns>Nothing, the ConnectedSystem passed in will be updated though with the new schema.</returns>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task ImportConnectedSystemSchemaAsync(ConnectedSystem connectedSystem, MetaverseObject initiatedBy)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        // every operation that results, either directly or indirectly in a data change requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.ImportSchema,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        // work out what connector we need to instantiate, so that we can use its internal validation method
        // 100% expecting this to be something we need to centralise/improve later as we develop the connector definition system
        // especially when we need to support uploaded connectors, not just built-in ones

        ConnectorSchema schema;
        if (connectedSystem.ConnectorDefinition.Name == Connectors.ConnectorConstants.LdapConnectorName)
            schema = await new LdapConnector().GetSchemaAsync(connectedSystem.SettingValues, Log.Logger);
        else if (connectedSystem.ConnectorDefinition.Name == Connectors.ConnectorConstants.FileConnectorName)
            schema = await new FileConnector().GetSchemaAsync(connectedSystem.SettingValues, Log.Logger);
        else
            throw new NotImplementedException("Support for that connector definition has not been implemented yet.");

        // this could potentially be a good point to check for data-loss if persisted and return a report object
        // that the user could use to decide if they need to take corrective steps, i.e. adjust attribute flow on sync rules.

        // super destructive at this point. this is for MVP only. will result in all prior  user object type and attribute selections to be lost!
        // todo: work out dependent changes required, i.e. sync rules will rely on connected system object type attributes. if they get removed from the schema
        // then we need to break any sync rule attribute flow relationships. this could be done gracefully to allow the user the opportunity to revise them, 
        // i.e. instead of just deleting the attribute flow and the user not knowing what they've lost, perhaps disable the attribute flow and leave a copy of the cs attrib name in place, 
        // so they can see it's not valid anymore and have information that will enable them to work out what to do about it.
        schema.ObjectTypes = schema.ObjectTypes.OrderBy(q => q.Name).ToList();
        connectedSystem.ObjectTypes = new List<ConnectedSystemObjectType>(); 
        foreach (var objectType in schema.ObjectTypes)
        {
            objectType.Attributes = objectType.Attributes.OrderBy(a => a.Name).ToList();
            var connectedSystemObjectType = new ConnectedSystemObjectType
            {
                Name = objectType.Name,
                Attributes = objectType.Attributes.Select(a => new ConnectedSystemObjectTypeAttribute
                {
                    Name = a.Name,
                    Description = a.Description,
                    AttributePlurality = a.AttributePlurality,
                    Type = a.Type,
                    ClassName = a.ClassName
                }).ToList()
            };

            // if there's an External Id attribute recommendation from the connector, use that. otherwise the user will have to pick one.
            var attribute = connectedSystemObjectType.Attributes.SingleOrDefault(a => objectType.RecommendedExternalIdAttribute != null && a.Name == objectType.RecommendedExternalIdAttribute.Name);
            if (attribute != null)
                attribute.IsExternalId = true;
            //else
            //   Log.Error($"A recommended External Id attribute '{objectType.RecommendedExternalIdAttribute.Name}' was not found in the objects list of attributes.");

            // if the connector supports it (requires it), take the secondary external id from the schema and mark the attribute as such
            if (connectedSystem.ConnectorDefinition.SupportsSecondaryExternalId && objectType.RecommendedSecondaryExternalIdAttribute != null)
            {
                var secondaryExternalIdAttribute = connectedSystemObjectType.Attributes.SingleOrDefault(a => a.Name == objectType.RecommendedSecondaryExternalIdAttribute.Name);
                if (secondaryExternalIdAttribute != null)
                    secondaryExternalIdAttribute.IsSecondaryExternalId = true;
                else
                    Log.Error($"Recommended Secondary External Id attribute '{objectType.RecommendedSecondaryExternalIdAttribute.Name}' was not found in the objects list of attributes!");
            }

            connectedSystem.ObjectTypes.Add(connectedSystemObjectType);
        }

        await UpdateConnectedSystemAsync(connectedSystem, initiatedBy, activity);

        // finish the activity
        await Application.Activities.CompleteActivityAsync(activity);
    }
    #endregion

    #region Connected System Hierarchy
    /// <summary>
    /// Causes the associated Connector to be instantiated and the hierarchy (partitions and containers) to be imported from the connected system.
    /// You will need update the ConnectedSystem after if happy with the changes, to persist them.
    /// </summary>
    /// <returns>Nothing, the ConnectedSystem passed in will be updated though with the new hierarchy.</returns>
    /// <remarks>Do not make static, it needs to be available on the instance</remarks>
    public async Task ImportConnectedSystemHierarchyAsync(ConnectedSystem connectedSystem, MetaverseObject initiatedBy)
    {
        ValidateConnectedSystemParameter(connectedSystem);

        // work out what connector we need to instantiate, so that we can use its internal validation method
        // 100% expecting this to be something we need to centralise/improve later as we develop the connector definition system
        // especially when we need to support uploaded connectors, not just built-in ones

        // every operation that results, either directly or indirectly in a data change requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystem.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.ImportHierarchy,
            ConnectedSystemId = connectedSystem.Id
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);

        List<ConnectorPartition> partitions;
        if (connectedSystem.ConnectorDefinition.Name == Connectors.ConnectorConstants.LdapConnectorName)
        {
            partitions = await new LdapConnector().GetPartitionsAsync(connectedSystem.SettingValues, Log.Logger);
            if (partitions.Count == 0)
            {
                // todo: report to the user we attempted to retrieve partitions, but got none back
            }
        }
        else
        {
            throw new NotImplementedException("Support for that connector definition has not been implemented yet.");
        }

        // this point could potentially be a good point to check for data-loss if persisted and return a report object
        // that the user could decide whether or not to take action against, i.e. cancel or persist.

        connectedSystem.Partitions = new List<ConnectedSystemPartition>(); // super destructive at this point. this is for mvp only. this causes all user partition/OU selections to be lost!
        foreach (var partition in partitions)
        {
            connectedSystem.Partitions.Add(new ConnectedSystemPartition
            {
                Name = partition.Name,
                ExternalId = partition.Id,
                Containers = partition.Containers.Select(BuildConnectedSystemContainerTree).ToHashSet()
            });
        }

        // for now though, we will just persist and let the user select containers later
        // pass in this user-initiated activity, so that sub-operations can be associated with it, i.e. the partition persisting operation
        await UpdateConnectedSystemAsync(connectedSystem, initiatedBy, activity);

        // finish the activity
        await Application.Activities.CompleteActivityAsync(activity);
    }

    private static ConnectedSystemContainer BuildConnectedSystemContainerTree(ConnectorContainer connectorContainer)
    {
        var connectedSystemContainer = new ConnectedSystemContainer
        {
            ExternalId = connectorContainer.Id,
            Name = connectorContainer.Name,
            Description = connectorContainer.Description,
            Hidden = connectorContainer.Hidden
        };

        foreach (var childContainer in connectorContainer.ChildContainers)
            connectedSystemContainer.AddChildContainer(BuildConnectedSystemContainerTree(childContainer));

        return connectedSystemContainer;
    }
    #endregion

    #region Connected System Object Types
    /// <summary>
    /// Retrieves all the Connected System Object Types for a given Connected System.
    /// Includes Attributes.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to return the types for.</param>
    public async Task<List<ConnectedSystemObjectType>> GetObjectTypesAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetObjectTypesAsync(connectedSystemId);
    }
    #endregion

    #region Connected System Objects
    /// <summary>
    /// Deletes a Connected System Object, and it's attribute values from a Connected System.
    /// Also prepares a Connected System Object Change for persistence with the activityRunProfileExecutionItem by the caller.  
    /// </summary>
    public async Task DeleteConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
    {
        await Application.Repository.ConnectedSystems.DeleteConnectedSystemObjectAsync(connectedSystemObject);
        
        // create a change object for this deletion
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = connectedSystemObject.ConnectedSystem.Id,
            ConnectedSystemObject = connectedSystemObject,
            ChangeType = ObjectChangeType.Delete,
            ChangeTime = DateTime.UtcNow,
            DeletedObjectType = connectedSystemObject.Type,
            DeletedObjectExternalIdAttributeValue = connectedSystemObject.ExternalIdAttributeValue,
            ActivityRunProfileExecutionItem = activityRunProfileExecutionItem
        };

        // the change object will be persisted with the activity run profile execution item further up the stack.
        // we just need to associate the change with the execution item.
        // unsure if this is the right approach. should we persist the change here and just associate with the detail item?
        activityRunProfileExecutionItem.ConnectedSystemObjectChange = change;
    }
    
    public async Task<List<string>> GetAllExternalIdAttributeValuesOfTypeStringAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeStringAsync(connectedSystemId, connectedSystemObjectTypeId);
    }
    
    public async Task<List<int>> GetAllExternalIdAttributeValuesOfTypeIntAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeIntAsync(connectedSystemId, connectedSystemObjectTypeId);
    }
    
    public async Task<List<Guid>> GetAllExternalIdAttributeValuesOfTypeGuidAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Application.Repository.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeGuidAsync(connectedSystemId, connectedSystemObjectTypeId);
    }
    
    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid id)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, id);
    }

    public async Task<PagedResultSet<ConnectedSystemObjectHeader>> GetConnectedSystemObjectHeadersAsync(int connectedSystemId, int page = 1, int pageSize = 20)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectHeadersAsync(connectedSystemId, page, pageSize);
    }
    
    /// <summary>
    /// Retrieves a page's worth of Connected System Objects for a specific system.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the system to return CSOs for.</param>
    /// <param name="page">Which page to return results for, i.e. 1-n.</param>
    /// <param name="pageSize">How many Connected System Objects to return in this page of result. By default it's 100.</param>
    /// <param name="returnAttributes">Controls whether ConnectedSystemObject.AttributeValues[n].Attribute is populated. By default, it isn't for performance reasons.</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public async Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsAsync(int connectedSystemId, int page = 1, int pageSize = 100, bool returnAttributes = false)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsAsync(connectedSystemId, page, pageSize, returnAttributes);
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, string attributeValue)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, connectedSystemAttributeId, attributeValue);
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, int attributeValue)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, connectedSystemAttributeId, attributeValue);
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, Guid attributeValue)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, connectedSystemAttributeId, attributeValue);
    }

    public async Task<Guid?> GetConnectedSystemObjectIdByAttributeValueAsync(int connectedSystemId, int connectedSystemAttributeId, string attributeValue)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectIdByAttributeValueAsync(connectedSystemId , connectedSystemAttributeId, attributeValue);
    }

    public async Task<int> GetConnectedSystemObjectCountAsync()
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountAsync();
    }
    
    /// <summary>
    /// Returns the count of Connected System Objects for a particular Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to find the object count for.</param>s
    public async Task<int> GetConnectedSystemObjectCountAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountAsync(connectedSystemId);
    }

    /// <summary>
    /// Creates a single Connected System Object and appends a Change Object to the Activity Run Profile Execution Item.
    /// </summary>
    public async Task CreateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
    {
        // persist the cso first.
        await Application.Repository.ConnectedSystems.CreateConnectedSystemObjectAsync(connectedSystemObject);
        
        // add a change object to the run profile execution item, so the change is logged.
        // it will be persisted higher up the calling stack as part of the Activity and its Activity Run Profile Execution Items.
        AddConnectedSystemObjectChange(connectedSystemObject, activityRunProfileExecutionItem);
    }

    /// <summary>
    /// Bulk persists Connected System Objects and appends a Change Object to the Activity Run Profile Execution Item.
    /// </summary>
    public async Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects, Activity activity)
    {
        // bulk persist csos creates
        await Application.Repository.ConnectedSystems.CreateConnectedSystemObjectsAsync(connectedSystemObjects);
        
        // add a Change Object to the relevant Activity Run Profile Execution Item for each cso.
        // they will be persisted further up the call stack, when the activity gets persisted.
        foreach (var cso in connectedSystemObjects)
        {
            var activityRunProfileExecutionItem = activity.RunProfileExecutionItems.SingleOrDefault(q => q.ConnectedSystemObject != null && q.ConnectedSystemObject.Id == cso.Id) ?? 
                                                  throw new InvalidDataException($"Couldn't find an ActivityRunProfileExecutionItem referencing CSO {cso.Id}! It should have been created further up the stack.");

            AddConnectedSystemObjectChange(cso, activityRunProfileExecutionItem);
        }
    }
    
    /// <summary>
    /// Bulk persists Connected System Object updates and appends a Change Object to the Activity Run Profile Execution Item for each one.
    /// </summary>
    public async Task UpdateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects, Activity activity)
    {
        // add a change object to the relevant activity run profile execution item for each cso to be updated.
        // the change objects will be persisted later, further up the call stack, when the activity gets persisted.
        foreach (var cso in connectedSystemObjects)
        {
            var activityRunProfileExecutionItem = activity.RunProfileExecutionItems.SingleOrDefault(q => q.ConnectedSystemObject != null && q.ConnectedSystemObject.Id == cso.Id) ?? 
                                                  throw new InvalidDataException($"Couldn't find an ActivityRunProfileExecutionItem referencing CSO {cso.Id}! It should have been created further up the stack.");
            
            ProcessConnectedSystemObjectAttributeValueChanges(cso, activityRunProfileExecutionItem);
        }
        
        // bulk persist csos updates
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemObjectsAsync(connectedSystemObjects);
    }

    /// <summary>
    /// Adds a Change Object to a Run Profile Execution Item for a CSO that's being created.
    /// </summary>
    private static void AddConnectedSystemObjectChange(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
    {
        // now populate the Connected System Object Change Object with the cso attribute values.
        // create a change object we can add attribute changes to.
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = connectedSystemObject.ConnectedSystem.Id,
            ConnectedSystemObject = connectedSystemObject,
            ChangeType = ObjectChangeType.Create,
            ChangeTime = DateTime.UtcNow,
            ActivityRunProfileExecutionItem = activityRunProfileExecutionItem,
            ActivityRunProfileExecutionItemId = activityRunProfileExecutionItem.Id
        };
        activityRunProfileExecutionItem.ConnectedSystemObjectChange = change;
        
        foreach (var attributeValue in connectedSystemObject.AttributeValues)
            AddChangeAttributeValueObject(change, attributeValue, ValueChangeType.Add);
    }

    /// <summary>
    /// Adds a Change object to a Un Profile Execution Item for a CSO that's being updated.
    /// </summary>
    private static void ProcessConnectedSystemObjectAttributeValueChanges(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
    {
        if (connectedSystemObject == null)
            throw new ArgumentNullException(nameof(connectedSystemObject));

        if (connectedSystemObject.AttributeValues.Any(csav => csav.Attribute == null))
            throw new ArgumentException($"One or more AttributeValue {nameof(ConnectedSystemObjectAttributeValue)} objects do not have an Attribute property set.", nameof(connectedSystemObject));

        if (connectedSystemObject.AttributeValues.Any(csav => csav.ConnectedSystemObject == null))
            throw new ArgumentException($"One or more AttributeValue {nameof(ConnectedSystemObjectAttributeValue)} objects do not have a ConnectedSystemObject property set.", nameof(connectedSystemObject));

        // check if there's any work to do. we need something in the pending attribute value additions, or removals to continue
        if (connectedSystemObject.PendingAttributeValueAdditions.Count == 0 && connectedSystemObject.PendingAttributeValueRemovals.Count == 0)
        {
            Log.Verbose($"UpdateConnectedSystemObjectAttributeValuesAsync: No work to do. No pending attribute value changes for CSO: {connectedSystemObject.Id}");
            return;
        }

        // create a change object we can track attribute changes with
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = connectedSystemObject.ConnectedSystem.Id,
            ConnectedSystemObject = connectedSystemObject,
            ChangeType = ObjectChangeType.Update,
            ChangeTime = DateTime.UtcNow,
            ActivityRunProfileExecutionItem = activityRunProfileExecutionItem
        };

        // the change object will be persisted with the activity run profile execution item further up the stack.
        // we just need to associate the change with the detail item.
        // unsure if this is the right approach. should we persist the change here and just associate with the detail item?
        activityRunProfileExecutionItem.ConnectedSystemObjectChange = change;

        // make sure the CSO is linked to the activity run profile execution item
        activityRunProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;

        // persist new attribute values from addition list and create change object
        foreach (var pendingAttributeValueAddition in connectedSystemObject.PendingAttributeValueAdditions)
        {
            connectedSystemObject.AttributeValues.Add(pendingAttributeValueAddition);
                
            // trigger auditing of this change
            AddChangeAttributeValueObject(change, pendingAttributeValueAddition, ValueChangeType.Add);
        }

        // delete attribute values to be removed and create change
        foreach (var pendingAttributeValueRemoval in connectedSystemObject.PendingAttributeValueRemovals)
        {
            // this will cause a cascade delete of the attribute value object
            connectedSystemObject.AttributeValues.RemoveAll(av => av.Id == pendingAttributeValueRemoval.Id);

            // trigger auditing of this change
            AddChangeAttributeValueObject(change, pendingAttributeValueRemoval, ValueChangeType.Remove);
        }
        
        // we can now reset the pending attribute value lists
        connectedSystemObject.PendingAttributeValueAdditions = new List<ConnectedSystemObjectAttributeValue>();
        connectedSystemObject.PendingAttributeValueRemovals = new List<ConnectedSystemObjectAttributeValue>();
    }

    /// <summary>
    /// Causes all of the connected system objects and pending export objects for a connected system to be deleted.
    /// Once performed, an admin must then re-synchronise all connectors to re-calculate any metaverse and connected system object changes to be sure of the intended state.
    /// </summary>
    /// <remarks>Only intended to be called by JIM.Service, i.e. this action should always be queued. That's why this method is lightweight and doesn't create it's own activity.</remarks>
    /// <param name="connectedSystemId">The unique identifier for the connected system to clear.</param>
    public async Task ClearConnectedSystemObjectsAsync(int connectedSystemId)
    {
        // delete all pending export objects
        Log.Verbose($"ClearConnectedSystemObjectsAsync: Deleting all pending export objects for connected system id {connectedSystemId}.");
        Application.Repository.ConnectedSystems.DeleteAllPendingExportObjects(connectedSystemId);

        // delete all connected system objects
        Log.Verbose($"ClearConnectedSystemObjectsAsync: Deleting all connected system objects for connected system id {connectedSystemId}.");
        await Application.Repository.ConnectedSystems.DeleteAllConnectedSystemObjectsAsync(connectedSystemId, true);

        // todo: think about returning a status to the UI. perhaps return the job id and allow the job status to be polled/streamed?
    }
        
    /// <summary>
    /// Creates the necessary attribute change audit item for when a CSO is created, updated, or deleted, and adds it to the change object.
    /// </summary>
    /// <param name="connectedSystemObjectChange">The ConnectedSystemObjectChange that's associated with a ActivityRunProfileExecutionItem (the audit object for a sync run).</param>
    /// <param name="connectedSystemObjectAttributeValue">The attribute and value pair for the new value.</param>
    /// <param name="valueChangeType">The type of change, i.e. CREATE/UPDATE/DELETE.</param>
    private static void AddChangeAttributeValueObject(ConnectedSystemObjectChange connectedSystemObjectChange, ConnectedSystemObjectAttributeValue connectedSystemObjectAttributeValue, ValueChangeType valueChangeType)
    {
        var attributeChange = connectedSystemObjectChange.AttributeChanges.SingleOrDefault(ac => ac.Attribute.Id == connectedSystemObjectAttributeValue.Attribute.Id);
        if (attributeChange == null)
        {
            // create the attribute change object that provides an audit trail of changes to a cso's attributes
            attributeChange = new ConnectedSystemObjectChangeAttribute
            {
                Attribute = connectedSystemObjectAttributeValue.Attribute,
                ConnectedSystemChange = connectedSystemObjectChange
            };
            connectedSystemObjectChange.AttributeChanges.Add(attributeChange);
        }

        switch (connectedSystemObjectAttributeValue.Attribute.Type)
        {
            case AttributeDataType.Text when connectedSystemObjectAttributeValue.StringValue != null:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, connectedSystemObjectAttributeValue.StringValue));
                break;
            case AttributeDataType.Number when connectedSystemObjectAttributeValue.IntValue != null:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, (int)connectedSystemObjectAttributeValue.IntValue));
                break;
            case AttributeDataType.Guid when connectedSystemObjectAttributeValue.GuidValue != null:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, (Guid)connectedSystemObjectAttributeValue.GuidValue));
                break;
            case AttributeDataType.Boolean when connectedSystemObjectAttributeValue.BoolValue != null:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, (bool)connectedSystemObjectAttributeValue.BoolValue));
                break;
            case AttributeDataType.DateTime when connectedSystemObjectAttributeValue.DateTimeValue.HasValue:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, connectedSystemObjectAttributeValue.DateTimeValue.Value));
                break;
            case AttributeDataType.Binary when connectedSystemObjectAttributeValue.ByteValue != null:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, true, connectedSystemObjectAttributeValue.ByteValue.Length));
                break;
            case AttributeDataType.Reference when connectedSystemObjectAttributeValue.ReferenceValue != null:
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, connectedSystemObjectAttributeValue.ReferenceValue));
                break;
            case AttributeDataType.Reference when connectedSystemObjectAttributeValue.UnresolvedReferenceValue != null:
                // we do not log changes for unresolved references. only resolved references get change tracked.
                break;
            case AttributeDataType.NotSet:
            default:
                throw new InvalidDataException($"AddChangeAttributeValueObject:  Invalid removal attribute '{connectedSystemObjectAttributeValue.Attribute.Name}' of type '{connectedSystemObjectAttributeValue.Attribute.Type}' or null attribute value.");
        }
    }

    public async Task<bool> IsObjectTypeAttributeBeingReferencedAsync(ConnectedSystemObjectTypeAttribute connectedSystemObjectTypeAttribute)
    {
        return await Application.Repository.ConnectedSystems.IsObjectTypeAttributeBeingReferencedAsync(connectedSystemObjectTypeAttribute);
    }
    #endregion

    #region Connected System Partitions
    public async Task CreateConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition)
    {
        if (connectedSystemPartition == null)
            throw new ArgumentNullException(nameof(connectedSystemPartition));

        await Application.Repository.ConnectedSystems.CreateConnectedSystemPartitionAsync(connectedSystemPartition);
    }

    public async Task<IList<ConnectedSystemPartition>> GetConnectedSystemPartitionsAsync(ConnectedSystem connectedSystem)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        return await Application.Repository.ConnectedSystems.GetConnectedSystemPartitionsAsync(connectedSystem);
    }

    public async Task DeleteConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition)
    {
        if (connectedSystemPartition == null)
            throw new ArgumentNullException(nameof(connectedSystemPartition));
        
        await Application.Repository.ConnectedSystems.DeleteConnectedSystemPartitionAsync(connectedSystemPartition);
    }
    #endregion

    #region Connected System Containers
    /// <summary>
    /// Used to create a top-level container (optionally with children), when the connector does not implement Partitions.
    /// If the connector implements Partitions, then use CreateConnectedSystemPartitionAsync and add the container to that.
    /// </summary>
    public async Task CreateConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer)
    {
        if (connectedSystemContainer == null)
            throw new ArgumentNullException(nameof(connectedSystemContainer));

        await Application.Repository.ConnectedSystems.CreateConnectedSystemContainerAsync(connectedSystemContainer);
    }

    public async Task<IList<ConnectedSystemContainer>> GetConnectedSystemContainersAsync(ConnectedSystem connectedSystem)
    {
        if (connectedSystem == null)
            throw new ArgumentNullException(nameof(connectedSystem));

        return await Application.Repository.ConnectedSystems.GetConnectedSystemContainersAsync(connectedSystem);
    }

    public async Task DeleteConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer)
    {
        if (connectedSystemContainer == null)
            throw new ArgumentNullException(nameof(connectedSystemContainer));


        await Application.Repository.ConnectedSystems.DeleteConnectedSystemContainerAsync(connectedSystemContainer);
    }
    #endregion

    #region Connected System Run Profiles
    public async Task CreateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile, MetaverseObject initiatedBy)
    {
        if (connectedSystemRunProfile == null)
            throw new ArgumentNullException(nameof(connectedSystemRunProfile));

        // need to get the connected system, so we can validate the run profile
        var connectedSystem = await GetConnectedSystemAsync(connectedSystemRunProfile.ConnectedSystemId) ?? throw new ArgumentException("No such Connected System found!");
        if (!IsRunProfileValid(connectedSystem, connectedSystemRunProfile))
            throw new ArgumentException("Run profile is not valid for the Connector!");

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystemRunProfile.Name,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Create,
            ConnectedSystemId = connectedSystemRunProfile.ConnectedSystemId
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        await Application.Repository.ConnectedSystems.CreateConnectedSystemRunProfileAsync(connectedSystemRunProfile);

        // now the run profile has been persisted, associated it with the activity and complete it.
        activity.ConnectedSystemRunProfileId = connectedSystemRunProfile.Id;
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task DeleteConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile, MetaverseObject initiatedBy)
    {
        if (connectedSystemRunProfile == null)
            return;

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetName = connectedSystemRunProfile.Name,
            ConnectedSystemRunType = connectedSystemRunProfile.RunType,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Delete,
            ConnectedSystemId = connectedSystemRunProfile.ConnectedSystemId
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        await Application.Repository.ConnectedSystems.DeleteConnectedSystemRunProfileAsync(connectedSystemRunProfile);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task UpdateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile, MetaverseObject initiatedBy)
    {
        if (connectedSystemRunProfile == null)
            throw new ArgumentNullException(nameof(connectedSystemRunProfile));

        // every CRUD operation requires tracking with an activity...
        var activity = new Activity
        {
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Update,
            ConnectedSystemRunProfileId = connectedSystemRunProfile.Id,
            ConnectedSystemId = connectedSystemRunProfile.ConnectedSystemId
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        await Application.Repository.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(connectedSystemRunProfile);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task<List<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(ConnectedSystem connectedSystem)
    {
        return await GetConnectedSystemRunProfilesAsync(connectedSystem.Id);
    }

    public async Task<List<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
    }

    public async Task<ConnectedSystemRunProfileHeader?> GetConnectedSystemRunProfileHeaderAsync(int connectedSystemRunProfileId)
    {
        return await Application.Repository.ConnectedSystems.GetConnectedSystemRunProfileHeaderAsync(connectedSystemRunProfileId);
    }

    /// <summary>
    /// Checks if any run profile types are not supported by the connectors capabilities.
    /// </summary>
    private static bool AreRunProfilesValid(ConnectedSystem connectedSystem)
    {
        if (connectedSystem == null)
            return false;

        if (connectedSystem.RunProfiles == null || connectedSystem.RunProfiles.Count == 0)
            return true;

        foreach (var runProfile in connectedSystem.RunProfiles)
        {
            if (!IsRunProfileValid(connectedSystem, runProfile))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if any run profile types are not supported by the connectors capabilities.
    /// </summary>
    private static bool IsRunProfileValid(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile)
    {
        if (runProfile == null)
            return false;

        if (runProfile.RunType == ConnectedSystemRunType.FullImport && !connectedSystem.ConnectorDefinition.SupportsFullImport)
            return false;

        if (runProfile.RunType == ConnectedSystemRunType.DeltaImport && !connectedSystem.ConnectorDefinition.SupportsDeltaImport)
            return false;

        if (runProfile.RunType == ConnectedSystemRunType.Export && !connectedSystem.ConnectorDefinition.SupportsExport)
            return false;

        return true;
    }
    #endregion
    
    #region Pending Exports
    /// <summary>
    /// Retrieves all the Pending Exports for a given Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System the Pending Exports relate to.</param>
    public async Task<List<PendingExport>> GetPendingExportsAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetPendingExportsAsync(connectedSystemId);
    }
    
    /// <summary>
    /// Retrieves the count of how many Pending Export objects there are for a particular Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System the Pending Exports relate to.</param>
    public async Task<int> GetPendingExportsCountAsync(int connectedSystemId)
    {
        return await Application.Repository.ConnectedSystems.GetPendingExportsCountAsync(connectedSystemId);
    }
    #endregion

    #region Sync Rules
    public async Task<List<SyncRule>> GetSyncRulesAsync()
    {
        return await Application.Repository.ConnectedSystems.GetSyncRulesAsync();
    }

    /// <summary>
    /// Retrieves all the sync rules for a given Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System.</param>
    /// <param name="includeDisabledSyncRules">Controls whether to return sync rules that are disabled</param>
    public async Task<List<SyncRule>> GetSyncRulesAsync(int connectedSystemId, bool includeDisabledSyncRules)
    {
        return await Application.Repository.ConnectedSystems.GetSyncRulesAsync(connectedSystemId, includeDisabledSyncRules);
    }

    public async Task<IList<SyncRuleHeader>> GetSyncRuleHeadersAsync()
    {
        return await Application.Repository.ConnectedSystems.GetSyncRuleHeadersAsync();
    }

    public async Task<SyncRule?> GetSyncRuleAsync(int id)
    {
        return await Application.Repository.ConnectedSystems.GetSyncRuleAsync(id);
    }

    public async Task<bool> CreateOrUpdateSyncRuleAsync(SyncRule syncRule, MetaverseObject initiatedBy, Activity? parentActivity = null)
    {
        // validate the sync rule
        if (syncRule == null)
            throw new NullReferenceException(nameof(syncRule));

        Log.Verbose($"CreateOrUpdateSyncRuleAsync() called for: {syncRule}");
        
        if (!syncRule.IsValid())
            return false;
        
        // remove any mutually-exclusive property combinations
        if (syncRule.Direction == SyncRuleDirection.Import)
        {
            // import rule cannot have these properties:
            syncRule.ObjectScopingCriteriaGroups.Clear();
            syncRule.ProvisionToConnectedSystem = null;
        }
        else
        {
            // export rule cannot have these properties:
            syncRule.ObjectMatchingRules.Clear();
            syncRule.ProjectToMetaverse = null;
        }
        
        // make sure attribute flow rules don't have an order set. that wouldn't be supported.
        var attributeFlowRulesWithOrders = syncRule.AttributeFlowRules.Where(q => q.Order != null);
        foreach (var afr in attributeFlowRulesWithOrders)
            afr.Order = null;
        
        // every crud operation must be tracked via an Activity
        var activity = new Activity
        {
            TargetName = syncRule.Name,
            TargetType = ActivityTargetType.SyncRule,
            ParentActivityId = parentActivity?.Id
        };

        if (syncRule.Id == 0)
        {
            // new sync rule - create
            activity.TargetOperationType = ActivityTargetOperationType.Create;
            syncRule.CreatedBy = initiatedBy;
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);
            await Application.Repository.ConnectedSystems.CreateSyncRuleAsync(syncRule);
        }
        else
        {
            // existing sync rule - update
            activity.TargetOperationType = ActivityTargetOperationType.Update;
            syncRule.LastUpdated = DateTime.UtcNow;
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);
            await Application.Repository.ConnectedSystems.UpdateSyncRuleAsync(syncRule);
        }

        await Application.Activities.CompleteActivityAsync(activity);
        return true;
    }

    public async Task DeleteSyncRuleAsync(SyncRule syncRule, MetaverseObject initiatedBy)
    {
        // every crud operation must be tracked via an Activity
        var activity = new Activity
        {
            TargetName = syncRule.Name,
            TargetType = ActivityTargetType.SyncRule,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        await Application.Repository.ConnectedSystems.DeleteSyncRuleAsync(syncRule);
        await Application.Activities.CompleteActivityAsync(activity);
    }
    #endregion
}
