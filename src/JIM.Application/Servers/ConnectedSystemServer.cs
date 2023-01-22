using JIM.Connectors.LDAP;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Logic.Dtos;
using JIM.Models.Staging;
using JIM.Models.Staging.Dtos;
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
        public async Task<IList<ConnectedSystem>> GetConnectedSystemsAsync()
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemsAsync();
        }

        public async Task<IList<ConnectedSystemHeader>> GetConnectedSystemHeadersAsync()
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
            var validationResults = await ValidateConnectedSystemSettingsAsync(connectedSystem);
            connectedSystem.SettingValuesValid = !validationResults.Any(q => q.IsValid == false);

            connectedSystem.LastUpdated = DateTime.Now;
            await Application.Repository.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem);
        }

        /// <summary>
        /// Use this when a connector is being parsed for persistence as a connector definition to create the connector definition settings from the connector instance.
        /// </summary>
        #pragma warning disable CA1822 // Mark members as static
        public void CopyConnectorSettingsToConnectorDefinition(IConnectorSettings connector, ConnectorDefinition connectorDefinition)
        #pragma warning restore CA1822 // Mark members as static
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

        #pragma warning disable CA1822 // Mark members as static
        public async Task<IList<ConnectorSettingValueValidationResult>> ValidateConnectedSystemSettingsAsync(ConnectedSystem connectedSystem)
        #pragma warning restore CA1822 // Mark members as static
        {
            if (connectedSystem == null)
                throw new ArgumentNullException(nameof(connectedSystem));

            if (connectedSystem.ConnectorDefinition == null)
                throw new ArgumentException(nameof(connectedSystem), "The supplied ConnectedSystem doesn't have a valid ConnectorDefinition.");

            if (connectedSystem.SettingValues == null || connectedSystem.SettingValues.Count == 0)
                throw new ArgumentException(nameof(connectedSystem), "The supplied ConnectedSystem doesn't have any valid SettingValues.");

            // work out what connector we need to instantiate, so that we can use its internal validation method
            // 100% expecting this to be something we need to centralise/improve later as we develop the connector definition system
            // especially when we need to support uploaded connectors, not just built-in ones

            if (connectedSystem.ConnectorDefinition.Name == Connectors.Constants.LdapConnectorName)
            {
                return await new LdapConnector().ValidateSettingValuesAsync(connectedSystem.SettingValues);
            }

            throw new NotImplementedException("Support for that connector definition has not been implemented.");
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

        public async Task DeleteConnectedSystemRunProfile(ConnectedSystemRunProfile connectedSystemRunProfile)
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

        public async Task<IList<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(ConnectedSystem connectedSystem)
        {
            return await GetConnectedSystemRunProfilesAsync(connectedSystem.Id);
        }

        public async Task<IList<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(int connectedSystemId)
        {
            return await Application.Repository.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
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
