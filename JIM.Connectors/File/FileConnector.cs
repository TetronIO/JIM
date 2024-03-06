using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Connectors.File
{
    public class FileConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorSchema, IConnectorImportUsingFiles
    {
        #region IConnector members
        public string Name => ConnectorConstants.FileConnectorName;

        public string? Description => "Enables bi-directional synchronisation with files, i.e. CSV.";

        public string? Url => "https://github.com/TetronIO/JIM";
        #endregion

        #region IConnectorCapabilities members
        public bool SupportsFullImport { get => true; }
        public bool SupportsDeltaImport { get => false; }
        public bool SupportsExport { get => false; }
        public bool SupportsPartitions { get => false; }
        public bool SupportsPartitionContainers { get => false; }
        public bool SupportsSecondaryExternalId { get => false; }
        #endregion

        #region IConnectorSettings members
        // variablising the names to reduce repitition later on, i.e. when we go to consume setting values JIM passes in, or when validating administrator-supplied settings
        private readonly string _settingFilePath = "File Path";

        public List<ConnectorSetting> GetSettings()
        {
            return new List<ConnectorSetting>
            {
                new() { Name = "File Details", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
                new() { Name = "File Details 2", Description = "Enter details on the file to read/write to below.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Label },
                new() { Name = _settingFilePath, Required = true, Description = "Supply a UNC full path to the file, i.e. \\\\fs001\\idam\\users.csv", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            };
        }

        /// <summary>
        /// Validates LdapConnector setting values using custom business logic.
        /// </summary>
        public List<ConnectorSettingValueValidationResult> ValidateSettingValues(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
        {
            Log.Verbose($"ValidateSettingValues() called for {Name}");
            var response = new List<ConnectorSettingValueValidationResult>();

            // validate that we can access the file with the supplied path
            if (!System.IO.File.Exists(_settingFilePath))
            {
                var connectivityTestResult = new ConnectorSettingValueValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "File either doesn't exist, or it couldn't be accessed."
                };
                response.Add(connectivityTestResult);
            }

            // general required setting value validation
            foreach (var requiredSettingValue in settingValues.Where(q => q.Setting.Required))
            {
                if (requiredSettingValue.Setting.Type == ConnectedSystemSettingType.String && string.IsNullOrEmpty(requiredSettingValue.StringValue))
                    response.Add(new ConnectorSettingValueValidationResult { ErrorMessage = $"Please supply a value for {requiredSettingValue.Setting.Name}", IsValid = false, SettingValue = requiredSettingValue });
            }

            return response;
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
