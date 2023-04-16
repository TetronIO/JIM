using JIM.Connectors.LDAP;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;

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

        public async Task CreateConnectedSystemAsync(ConnectedSystem connectedSystem)
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
                var settingValue = new ConnectedSystemSettingValue
                {
                    Setting = connectedSystemDefinitionSetting
                };

                if (connectedSystemDefinitionSetting.Type == ConnectedSystemSettingType.CheckBox && connectedSystemDefinitionSetting.DefaultCheckboxValue != null)
                    settingValue.CheckboxValue = connectedSystemDefinitionSetting.DefaultCheckboxValue.Value;

                if (connectedSystemDefinitionSetting.Type == ConnectedSystemSettingType.String && !string.IsNullOrEmpty(connectedSystemDefinitionSetting.DefaultStringValue))
                    settingValue.StringValue = connectedSystemDefinitionSetting.DefaultStringValue;

                if (connectedSystemDefinitionSetting.Type == ConnectedSystemSettingType.Integer && connectedSystemDefinitionSetting.DefaultIntValue.HasValue)
                    settingValue.IntValue = connectedSystemDefinitionSetting.DefaultIntValue.Value;

                connectedSystem.SettingValues.Add(settingValue);
            }

            await Application.Repository.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem);
        }

        public async Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem)
        {
            if (connectedSystem == null)
                throw new ArgumentNullException(nameof(connectedSystem));

            // are the settings valid?
            var validationResults = ValidateConnectedSystemSettings(connectedSystem);
            connectedSystem.SettingValuesValid = !validationResults.Any(q => q.IsValid == false);

            connectedSystem.LastUpdated = DateTime.Now;
            await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);
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

            if (connectedSystem.ConnectorDefinition.Name == Connectors.Constants.LdapConnectorName)
            {
                return new LdapConnector().ValidateSettingValues(connectedSystem.SettingValues);
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
        /// You will need update the ConnectedSystem after if happy with the changes.
        /// </summary>
        /// <returns>Nothing, the ConnectedSystem passed in will be updated though with the new schema.</returns>
        /// <remarks>Do not make static, it needs to be available on the instance</remarks>
        public async Task ImportConnectedSystemSchemaAsync(ConnectedSystem connectedSystem)
        {
            ValidateConnectedSystemParameter(connectedSystem);

            // work out what connector we need to instantiate, so that we can use its internal validation method
            // 100% expecting this to be something we need to centralise/improve later as we develop the connector definition system
            // especially when we need to support uploaded connectors, not just built-in ones

            ConnectorSchema schema;
            if (connectedSystem.ConnectorDefinition.Name == Connectors.Constants.LdapConnectorName)
            {
                schema = await new LdapConnector().GetSchemaAsync(connectedSystem.SettingValues);
            }
            else
            {
                throw new NotImplementedException("Support for that connector definition has not been implemented yet.");
            }

            // this point could potentially be a good point to check for data-loss if persisted and return a report object
            // that the user could decide whether or not to take action upon, i.e. cancel or persist.

            connectedSystem.ObjectTypes.Clear(); // super destructive at this point. this is for mvp only
            foreach (var objectType in schema.ObjectTypes)
            {
                connectedSystem.ObjectTypes.Add(new ConnectedSystemObjectType
                {
                    Name = objectType.Name,
                    Attributes = objectType.Attributes.Select(q => new ConnectedSystemAttribute
                    {
                        Name = q.Name,
                        Description = q.Description,
                        AttributePlurality = q.AttributePlurality,
                        Type = q.Type,
                        ClassName = q.ClassName
                    }).ToList()
                });
            }
        }
        #endregion

        #region Connected System Hierarchy
        /// <summary>
        /// Causes the associated Connector to be instantiated and the hierarchy (partitions and containers) to be imported from the connected system.
        /// You will need update the ConnectedSystem after if happy with the changes, to persist them.
        /// </summary>
        /// <returns>Nothing, the ConnectedSystem passed in will be updated though with the new hierarchy.</returns>
        /// <remarks>Do not make static, it needs to be available on the instance</remarks>
        #pragma warning disable CA1822 // Mark members as static
        public async Task ImportConnectedSystemHierarchyAsync(ConnectedSystem connectedSystem)
        #pragma warning restore CA1822 // Mark members as static
        {
            ValidateConnectedSystemParameter(connectedSystem);

            // work out what connector we need to instantiate, so that we can use its internal validation method
            // 100% expecting this to be something we need to centralise/improve later as we develop the connector definition system
            // especially when we need to support uploaded connectors, not just built-in ones

            List<ConnectorPartition> partitions;
            if (connectedSystem.ConnectorDefinition.Name == Connectors.Constants.LdapConnectorName)
            {
                partitions = await new LdapConnector().GetPartitionsAsync(connectedSystem.SettingValues);
            }
            else
            {
                throw new NotImplementedException("Support for that connector definition has not been implemented yet.");
            }

            // this point could potentially be a good point to check for data-loss if persisted and return a report object
            // that the user could decide whether or not to take action against, i.e. cancel or persist.

            connectedSystem.Partitions = new List<ConnectedSystemPartition>(); // super destructive at this point. this is for mvp only
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
            await UpdateConnectedSystemAsync(connectedSystem);
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

        public async Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, int id)
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, id);
        }

        public async Task<int> GetConnectedSystemObjectCountAsync()
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectCountAsync();
        }

        public async Task<int> GetConnectedSystemObjectOfTypeCountAsync(ConnectedSystemObjectType connectedSystemObjectType)
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemObjectOfTypeCountAsync(connectedSystemObjectType.Id);
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
        public async Task CreateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile)
        {
            if (connectedSystemRunProfile == null)
                throw new ArgumentNullException(nameof(connectedSystemRunProfile));

            await Application.Repository.ConnectedSystems.CreateConnectedSystemRunProfileAsync(connectedSystemRunProfile);
        }

        public async Task DeleteConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile)
        {
            if (connectedSystemRunProfile == null)
                return;

            await Application.Repository.ConnectedSystems.DeleteConnectedSystemRunProfileAsync(connectedSystemRunProfile);
        }

        public async Task UpdateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile)
        {
            if (connectedSystemRunProfile == null)
                throw new ArgumentNullException(nameof(connectedSystemRunProfile));

            await Application.Repository.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(connectedSystemRunProfile);
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

        #region Synchronisation Runs
        public async Task<IList<SyncRun>?> GetSynchronisationRunsAsync(int id)
        {
            return await Application.Repository.ConnectedSystems.GetSynchronisationRunsAsync(id);
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
