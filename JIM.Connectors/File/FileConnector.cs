using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JIM.Connectors.File
{
    public class FileConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorSchema, IConnectorImportUsingFiles
    {
        #region IConnector members
        public string Name => throw new NotImplementedException();

        public string? Description => throw new NotImplementedException();

        public string? Url => throw new NotImplementedException();
        #endregion

        #region IConnectorCapabilities members
        public bool SupportsFullImport => throw new NotImplementedException();

        public bool SupportsDeltaImport => throw new NotImplementedException();

        public bool SupportsExport => throw new NotImplementedException();

        public bool SupportsPartitions => throw new NotImplementedException();

        public bool SupportsPartitionContainers => throw new NotImplementedException();

        public bool SupportsSecondaryExternalId => throw new NotImplementedException();
        #endregion

        #region IConnectorSettings members
        public List<ConnectorSetting> GetSettings()
        {
            throw new NotImplementedException();
        }

        public List<ConnectorSettingValueValidationResult> ValidateSettingValues(List<ConnectedSystemSettingValue> settings, ILogger logger)
        {
            throw new NotImplementedException();
        }
        #endregion

        #region IConnectorSchema members
        public Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settings, ILogger logger)
        {
            throw new NotImplementedException();
        }
        #endregion

        #region IConnectorImportUsingFiles members
        public ConnectedSystemImportResult Import(IList<ConnectedSystemSettingValue> settings, ConnectedSystemRunProfile runProfile)
        {
            throw new NotImplementedException();
        }
        #endregion
    }
}
