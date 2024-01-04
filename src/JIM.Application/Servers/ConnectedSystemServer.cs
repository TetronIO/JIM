using JIM.Connectors.LDAP;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using Serilog;

namespace JIM.Application.Servers
{
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

        public async Task CreateConnectedSystemAsync(ConnectedSystem connectedSystem, MetaverseObject initiatedBy)
        {
            if (connectedSystem == null)
                throw new ArgumentNullException(nameof(connectedSystem));

            if (connectedSystem.ConnectorDefinition == null)
                throw new ArgumentException("connectedSystem.ConnectorDefinition is null!");

            if (connectedSystem.ConnectorDefinition.Settings == null || connectedSystem.ConnectorDefinition.Settings.Count == 0)
                throw new ArgumentException("connectedSystem.ConnectorDefinition has no settings. Cannot construct a valid connectedSystem object!");

            // create the connected system setting value objects from the connected system definition settings
            foreach (var connectedSystemDefinitionSetting in connectedSystem.ConnectorDefinition.Settings)
            {
                var settingValue = new ConnectedSystemSettingValue {
                    Setting = connectedSystemDefinitionSetting
                };

                if (connectedSystemDefinitionSetting.Type == ConnectedSystemSettingType.CheckBox && connectedSystemDefinitionSetting.DefaultCheckboxValue != null)
                    settingValue.CheckboxValue = connectedSystemDefinitionSetting.DefaultCheckboxValue.Value;

                if (connectedSystemDefinitionSetting.Type == ConnectedSystemSettingType.String && !string.IsNullOrEmpty(connectedSystemDefinitionSetting.DefaultStringValue))
                    settingValue.StringValue = connectedSystemDefinitionSetting.DefaultStringValue.Trim();

                if (connectedSystemDefinitionSetting.Type == ConnectedSystemSettingType.Integer && connectedSystemDefinitionSetting.DefaultIntValue.HasValue)
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

            // are the settings valid?
            var validationResults = ValidateConnectedSystemSettings(connectedSystem);
            connectedSystem.SettingValuesValid = !validationResults.Any(q => q.IsValid == false);

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
            {
                return new LdapConnector().ValidateSettingValues(connectedSystem.SettingValues, Log.Logger);
            }

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
            else
                throw new NotImplementedException("Support for that connector definition has not been implemented yet.");

            // this could potentially be a good point to check for data-loss if persisted and return a report object
            // that the user could use to decide if they need to take corrective steps, i.e. adjust attribute flow on sync rules.

            // super destructive at this point. this is for MVP only. will result in all prior  user object type and attribute selections to be lost!
            // todo: work out dependent changes required, i.e. sync rules will rely on connected system object type attributes. if they get removed from the schema
            // then we need to break any sync rule attribute flow relationships. this could be done gracefully to allow the user the opportunity to revise them, 
            // i.e. instead of just deleting the attribute flow and the user not knowing what they've lost, perhaps disable the attribute flow and leave a copy of the cs attrib name in place, 
            // so they can see it's not valid anymore and have information that will enable them to work out what to do about it.
            connectedSystem.ObjectTypes = new List<ConnectedSystemObjectType>(); 
            foreach (var objectType in schema.ObjectTypes)
            {
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

                // take the External Id attribute recommendation as the default, and allow the user to potentially change it later if they want/need
                var attribute = connectedSystemObjectType.Attributes.SingleOrDefault(a => a.Name == objectType.RecommendedExternalIdAttribute.Name);
                if (attribute != null)
                    attribute.IsExternalId = true;
                else
                    Log.Error($"Recommended External Id attribute '{objectType.RecommendedExternalIdAttribute.Name}' was not found in the objects list of attributes!");

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
                    Containers = partition.Containers.Select(cc => BuildConnectedSystemContainerTree(cc)).ToHashSet()
                });
            }

            // for now though, we will just persist and let the user select containers later
            // pass in this user-initiated activity, so that sub-operations can be associated with it, i.e. the partition persiting operation
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
        public async Task<IList<ConnectedSystemObjectType>?> GetObjectTypesAsync(int id)
        {
            return await Application.Repository.ConnectedSystems.GetObjectTypesAsync(id);
        }
        #endregion

        #region Connected System Objects
        public async Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid id)
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, id);
        }

        public async Task<PagedResultSet<ConnectedSystemObjectHeader>> GetConnectedSystemObjectHeadersAsync(
            int connectedSystemId,
            int page = 1,
            int pageSize = 20,
            int maxResults = 500)
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectHeadersAsync(
                connectedSystemId,
                page,
                pageSize,
                maxResults);
        }

        public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByExternalIdAsync(int connectedSystemId, int connectedSystemAttributeId, string attributeValue)
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectByExternalIdAsync(connectedSystemId, connectedSystemAttributeId, attributeValue);
        }

        public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByExternalIdAsync(int connectedSystemId, int connectedSystemAttributeId, int attributeValue)
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectByExternalIdAsync(connectedSystemId, connectedSystemAttributeId, attributeValue);
        }

        public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByExternalIdAsync(int connectedSystemId, int connectedSystemAttributeId, Guid attributeValue)
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectByExternalIdAsync(connectedSystemId, connectedSystemAttributeId, attributeValue);
        }

        public async Task<int> GetConnectedSystemObjectCountAsync()
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountAsync();
        }

        public async Task<int> GetConnectedSystemObjectOfTypeCountAsync(ConnectedSystemObjectType connectedSystemObjectType)
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectOfTypeCountAsync(connectedSystemObjectType.Id);
        }

        public async Task CreateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
        {
            await Application.Repository.ConnectedSystems.CreateConnectedSystemObjectAsync(connectedSystemObject);

            // now populate the activity run profile execution item change object with the cso attribute values
            // create a change object we can add attribute changes to
            var change = new ConnectedSystemObjectChange
            {
                ConnectedSystemId = connectedSystemObject.ConnectedSystem.Id,
                ConnectedSystemObject = connectedSystemObject,
                ChangeType = ObjectChangeType.Create,
                ActivityRunProfileExecutionItem = activityRunProfileExecutionItem,
                ActivityRunProfileExecutionItemId = activityRunProfileExecutionItem.Id
            };
            activityRunProfileExecutionItem.ConnectedSystemObjectChange = change;

            foreach (var attributeValue in connectedSystemObject.AttributeValues)
                AddChangeAttributeValueObject(change, attributeValue, ValueChangeType.Add);

            // don't persist the activity run profile exection, let that be done further up the stack in bulk for efficiency.
        }

        public async Task UpdateConnectedSystemObjectAttributeValuesAsync(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
        {
            if (connectedSystemObject == null)
                throw new ArgumentNullException(nameof(connectedSystemObject));

            if (connectedSystemObject.AttributeValues.Any(csav => csav.Attribute == null))
                throw new ArgumentException($"One or more AttributeValue {nameof(ConnectedSystemObjectAttributeValue)} objects do not have an Attribute property set.", nameof(connectedSystemObject));

            if (connectedSystemObject.AttributeValues.Any(csav => csav.ConnectedSystemObject == null))
                throw new ArgumentException($"One or more AttributeValue {nameof(ConnectedSystemObjectAttributeValue)} objects do not have a ConnectedSystemObject property set.", nameof(connectedSystemObject));

            // check if there's any work to do. we need something in the pending attribute value additions, or removals to continue
            if ((connectedSystemObject.PendingAttributeValueAdditions == null || connectedSystemObject.PendingAttributeValueAdditions.Count == 0) &&
                (connectedSystemObject.PendingAttributeValueRemovals == null || connectedSystemObject.PendingAttributeValueRemovals.Count == 0))
            {
                Log.Verbose($"UpdateConnectedSystemObjectAttributeValuesAsync: No work to do. No pending attribute value changes for CSO: {connectedSystemObject.Id}");
                return;
            }

            // create a change object we can add attribute changes to
            var change = new ConnectedSystemObjectChange
            {
                ConnectedSystemId = connectedSystemObject.ConnectedSystem.Id,
                ConnectedSystemObject = connectedSystemObject,
                ChangeType = ObjectChangeType.Update,
                ActivityRunProfileExecutionItem = activityRunProfileExecutionItem
            };

            // the change object will be persisted by the activity run profile execution item further up the stack
            // we just need to associate the change with the detail item.
            // unsure if this is the right approach. should we persist the change here and just associate with the detail item?
            activityRunProfileExecutionItem.ConnectedSystemObjectChange = change;

            // make sure the CSO is linked to the activity run profile execution item
            activityRunProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;

            // persist new attribute values from addition list and create change object
            if (connectedSystemObject.PendingAttributeValueAdditions != null)
            {
                foreach (var pendingAttributeValueAddition in connectedSystemObject.PendingAttributeValueAdditions)
                {
                    connectedSystemObject.AttributeValues.Add(pendingAttributeValueAddition);
                    
                    // trigger auditing of this change
                    AddChangeAttributeValueObject(change, pendingAttributeValueAddition, ValueChangeType.Add);
                }
            }

            // delete attribute values to be removed and create change
            if (connectedSystemObject.PendingAttributeValueRemovals != null)
            {
                foreach (var pendingAttributeValueRemoval in connectedSystemObject.PendingAttributeValueRemovals)
                {
                    // this will cause a cascade delete of the attribute value object
                    connectedSystemObject.AttributeValues.RemoveAll(av => av.Id == pendingAttributeValueRemoval.Id);

                    // trigger auditing of this change
                    AddChangeAttributeValueObject(change, pendingAttributeValueRemoval, ValueChangeType.Remove);
                }
            }

            // don't persist the activity run profile exection, let that be done further up the stack in bulk for efficiency.

            // update the cso, which will create/delete the attribute value objects
            await Application.Repository.ConnectedSystems.UpdateConnectedSystemObjectAsync(connectedSystemObject);

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
                // create the attribute change object
                attributeChange = new ConnectedSystemObjectChangeAttribute
                {
                    Attribute = connectedSystemObjectAttributeValue.Attribute,
                    ConnectedSystemChange = connectedSystemObjectChange
                };
                connectedSystemObjectChange.AttributeChanges.Add(attributeChange);
            }

            if (connectedSystemObjectAttributeValue.Attribute.Type == AttributeDataType.Text && connectedSystemObjectAttributeValue.StringValue != null)
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, connectedSystemObjectAttributeValue.StringValue));
            else if (connectedSystemObjectAttributeValue.Attribute.Type == AttributeDataType.Number && connectedSystemObjectAttributeValue.IntValue != null)
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, (int)connectedSystemObjectAttributeValue.IntValue));
            else if (connectedSystemObjectAttributeValue.Attribute.Type == AttributeDataType.Guid && connectedSystemObjectAttributeValue.GuidValue != null)
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, (Guid)connectedSystemObjectAttributeValue.GuidValue));
            else if (connectedSystemObjectAttributeValue.Attribute.Type == AttributeDataType.Boolean && connectedSystemObjectAttributeValue.BoolValue != null)
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, (bool)connectedSystemObjectAttributeValue.BoolValue));
            else if (connectedSystemObjectAttributeValue.Attribute.Type == AttributeDataType.Binary && connectedSystemObjectAttributeValue.ByteValue != null)
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, true, connectedSystemObjectAttributeValue.ByteValue.Length));
            else if (connectedSystemObjectAttributeValue.Attribute.Type == AttributeDataType.Reference && connectedSystemObjectAttributeValue.StringValue != null) 
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, connectedSystemObjectAttributeValue.StringValue));
            else
                throw new InvalidDataException("AddChangeAttributeValueObject:  Invalid removal attribute type or null attribute value");
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
        #endregion

        #region Sync Rules
        public async Task<IList<SyncRule>> GetSyncRulesAsync()
        {
            return await Application.Repository.ConnectedSystems.GetSyncRulesAsync();
        }

        public async Task<IList<SyncRuleHeader>> GetSyncRuleHeadersAsync()
        {
            return await Application.Repository.ConnectedSystems.GetSyncRuleHeadersAsync();
        }

        public async Task<SyncRule?> GetSyncRuleAsync(int id)
        {
            return await Application.Repository.ConnectedSystems.GetSyncRuleAsync(id);
        }
        #endregion
    }
}
