using CsvHelper;
using CsvHelper.Configuration;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;
using System.Globalization;

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
        public bool SupportsFullImport => true;
        public bool SupportsDeltaImport => false;
        public bool SupportsExport => false;
        public bool SupportsPartitions => false;
        public bool SupportsPartitionContainers => false;
        public bool SupportsSecondaryExternalId => false;
        public bool SupportsUserSelectedExternalId => true;
        public bool SupportsUserSeletedAttributeTypes => true;
        #endregion

        #region IConnectorSettings members
        // variablising the names to reduce repitition later on, i.e. when we go to consume setting values JIM passes in, or when validating administrator-supplied settings
        private readonly string _settingExampleFilePath = "Example File Path";
        private readonly string _settingObjectTypeColumn = "Object Type Column";
        private readonly string _settingObjectType = "Object Type";
        private readonly string _settingCulture = "Culture";
        private readonly string _settingDelimiter = "Delimiter";

        public List<ConnectorSetting> GetSettings()
        {
            return new List<ConnectorSetting>
            {
                new() { Name = _settingExampleFilePath, Required = true, Description = "Supply the path to the example file in the container. The container path is determined by the Docker Volume configuration item. i.e. /var/connector-files/Users.csv", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new() { Name = _settingObjectTypeColumn, Required = false, Description = "Optionally specify the column that contains the object type.", Category = ConnectedSystemSettingCategory.Schema, Type = ConnectedSystemSettingType.String },
                new() { Name = _settingObjectType, Required = false, Description = "Optionally specify a fixed object type, i.e. the file only contains Users.", Category = ConnectedSystemSettingCategory.Schema, Type = ConnectedSystemSettingType.String },
                new() { Name = _settingDelimiter, Required = false, Description = "What character to use as the delimiter?", DefaultStringValue=",", Category = ConnectedSystemSettingCategory.Schema, Type = ConnectedSystemSettingType.String },
                new() { Name = _settingCulture, Required = false, Description = "Optionally specify a culture (i.e. en-gb) for the file contents. Use if you experience problems with the default (invariant culture).", Category = ConnectedSystemSettingCategory.Schema, Type = ConnectedSystemSettingType.String }
            };
        }

        /// <summary>
        /// Validates LdapConnector setting values using custom business logic.
        /// </summary>
        public List<ConnectorSettingValueValidationResult> ValidateSettingValues(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
        {
            logger.Verbose($"ValidateSettingValues() called for {Name}");
            var response = new List<ConnectorSettingValueValidationResult>();

            // general required setting value validation
            foreach (var requiredSettingValue in settingValues.Where(q => q.Setting.Required))
            {
                if (requiredSettingValue.Setting.Type == ConnectedSystemSettingType.String && string.IsNullOrEmpty(requiredSettingValue.StringValue))
                    response.Add(new ConnectorSettingValueValidationResult { ErrorMessage = $"Please supply a value for {requiredSettingValue.Setting.Name}", IsValid = false, SettingValue = requiredSettingValue });
            }

            // test that we can access the file
            var filePathSettingValue = settingValues.Single(q => q.Setting.Name == _settingExampleFilePath);
            if (!string.IsNullOrEmpty(filePathSettingValue.StringValue))
            {
                if (!System.IO.File.Exists(filePathSettingValue.StringValue))
                {
                    var connectivityTestResult = new ConnectorSettingValueValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"File either doesn't exist, or it couldn't be accessed. Does '{filePathSettingValue.StringValue}' map to a Docker Volume in the docker-compose.yml file?"
                    };
                    response.Add(connectivityTestResult);
                }
            }

            return response;
        }
        #endregion

        #region IConnectorSchema members
        /// <summary>
        /// Determine the file schema by inspecting some of the headers and row fields.
        /// </summary>
        public async Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
        {
            var exampleFilePath = settingValues.SingleOrDefault(q => q.Setting.Name == _settingExampleFilePath);
            if (exampleFilePath == null || string.IsNullOrEmpty(exampleFilePath.StringValue))
                throw new InvalidSettingValuesException($"Missing setting value for {_settingExampleFilePath}.");

            var delimiter = settingValues.SingleOrDefault(q => q.Setting.Name == _settingDelimiter);
            if (delimiter == null || string.IsNullOrEmpty(delimiter.StringValue))
                throw new InvalidSettingValuesException($"Missing setting value for {_settingDelimiter}.");

            var objectTypeColumn = settingValues.SingleOrDefault(q => q.Setting.Name == _settingObjectTypeColumn);
            var objectType = settingValues.SingleOrDefault(q => q.Setting.Name == _settingObjectType);
            if ((objectType == null || string.IsNullOrEmpty(objectType.StringValue)) && (objectTypeColumn == null || string.IsNullOrEmpty(objectTypeColumn.StringValue)))
                throw new InvalidSettingValuesException($"Either a {_settingObjectTypeColumn} or {_settingObjectType} need a setting value specifying.");

            // default culture info is invariant culture. hoping this is fine for most data.
            var cultureInfo = CultureInfo.InvariantCulture;

            // though the user can specify a specific culture if they're having problems with characters not being transferred between systems correctly.
            var culture = settingValues.SingleOrDefault(q => q.Setting.Name == _settingCulture);
            if (culture != null && !string.IsNullOrEmpty(culture.StringValue))
                cultureInfo = new CultureInfo(culture.StringValue);

            var config = new CsvConfiguration(CultureInfo.CurrentCulture) { Delimiter = delimiter.StringValue };
            logger.Debug($"GetSchemaAsync: Attempting to read '{exampleFilePath.StringValue}'");
            using var reader = new StreamReader(exampleFilePath.StringValue);
            using var csv = new CsvReader(reader, config);

            await csv.ReadAsync();
            csv.ReadHeader();
            var columnNames = csv.HeaderRecord;

            // start building the schema by inspecting the file!
            var schema = new ConnectorSchema();

            // the user can specify either a column to infer the object type of a row (needed when files contain reference attributes)
            // or they can specify a fixed object type, i.e. for when a file only contains a single object type.

            if (objectType != null && !string.IsNullOrEmpty(objectType.StringValue))
            {
                var schemaObjectType = new ConnectorSchemaObjectType(objectType.StringValue);
                schema.ObjectTypes.Add(schemaObjectType);
            }
            else
            {
                // todo: inspect row data for all the possible object types
            }

            // now determine the attributes from the file headers.
            // at this point we don't know what attributes are for what object type. so all object types get the same attributes.
            // later, the user can refine the per-object type attribute lists.
            foreach (var schemaObjectType in schema.ObjectTypes)
            {
                for (var i = 0; i < columnNames.Length; i++)
                {
                    // has this attribute already been added? if it has, this indicates it's a multi-valued attribute
                    var existingSchemaAttribute = schemaObjectType.Attributes.SingleOrDefault(q => q.Name.Equals(columnNames[i], StringComparison.OrdinalIgnoreCase));
                    if (existingSchemaAttribute != null)
                    {
                        if (existingSchemaAttribute.AttributePlurality != AttributePlurality.MultiValued)
                            existingSchemaAttribute.AttributePlurality = AttributePlurality.MultiValued;

                        continue;
                    }

                    // initially set the attributes with just a name. we'll work out their data types next.
                    schemaObjectType.Attributes.Add(new ConnectorSchemaAttribute(columnNames[i], AttributeDataType.NotSet, AttributePlurality.SingleValued));
                }
            }

            // read some rows and infer the data type of the fields
            var maxRowsToInspect = 50;
            var rowsInspected = 0;
            while (await csv.ReadAsync())
            {
                if (rowsInspected == maxRowsToInspect)
                    break;

                var schemaObjectType = schema.ObjectTypes[0];
                foreach (var schemaAttribute in schemaObjectType.Attributes)
                {
                    var field = csv.GetField(schemaAttribute.Name);

                    // some fields may be null/empty, skip those and hopefully we'll find
                    // a value we can inspect in a later row.
                    if (field == null || string.IsNullOrEmpty(field))
                        continue;

                    // attempt to infer the data type
                    // conflating ints and doubles may turn out to be a bad idea
                    if (int.TryParse(field, out var fieldInt) || double.TryParse(field, out var fieldDouble))
                        schemaAttribute.Type = AttributeDataType.Number;
                    else if (bool.TryParse(field, out var fieldBool))
                        schemaAttribute.Type = AttributeDataType.Boolean;
                    else if (Guid.TryParse(field, out var fieldGuid))
                        schemaAttribute.Type = AttributeDataType.Guid;
                    else if (DateTime.TryParse(field, out var fieldDate))
                        schemaAttribute.Type = AttributeDataType.DateTime;
                    else
                        schemaAttribute.Type = AttributeDataType.Text;

                    // if all fields have a type definition, stop inspecting
                    if (schemaObjectType.Attributes.All(a => a.Type != AttributeDataType.NotSet))
                        break;
                }

                rowsInspected++;
            }

            return schema;
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
