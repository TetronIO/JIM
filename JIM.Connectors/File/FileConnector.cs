using CsvHelper;
using CsvHelper.Configuration;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;
using System.Globalization;
namespace JIM.Connectors.File;

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
    public bool SupportsUserSelectedAttributeTypes => true;
    #endregion

    #region IConnectorSettings members
    // using member variables for the names to reduce repetition later on, i.e. when we go to consume setting values JIM passes in, or when validating administrator-supplied settings
    private const string SettingExampleFilePath = "Example File Path";
    private const string SettingObjectTypeColumn = "Object Type Column";
    private const string SettingObjectType = "Object Type";
    private const string SettingCulture = "Culture";
    private const string SettingDelimiter = "Delimiter";
    private const string SettingStopOnFirstError = "Stop On First Error";

    public List<ConnectorSetting> GetSettings()
    {
        return new List<ConnectorSetting>
        {
            new() { Name = SettingExampleFilePath, Required = true, Description = "Supply the path to the example file in the container. The container path is determined by the Docker Volume configuration item. i.e. /var/connector-files/Users.csv", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = SettingObjectTypeColumn, Required = false, Description = "Optionally specify the column that contains the object type.", Category = ConnectedSystemSettingCategory.Schema, Type = ConnectedSystemSettingType.String },
            new() { Name = SettingObjectType, Required = false, Description = "Optionally specify a fixed object type, i.e. the file only contains Users.", Category = ConnectedSystemSettingCategory.Schema, Type = ConnectedSystemSettingType.String },
            new() { Name = SettingDelimiter, Required = false, Description = "What character to use as the delimiter?", DefaultStringValue=",", Category = ConnectedSystemSettingCategory.Schema, Type = ConnectedSystemSettingType.String },
            new() { Name = SettingCulture, Required = false, Description = "Optionally specify a culture (i.e. en-gb) for the file contents. Use if you experience problems with the default (invariant culture).", Category = ConnectedSystemSettingCategory.Schema, Type = ConnectedSystemSettingType.String },
            new() { Name = SettingStopOnFirstError, Required = false, Description = "Stop processing the file when the first error is encountered. Useful for debugging data quality issues without generating large numbers of errors.", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.CheckBox }
        };
    }

    /// <summary>
    /// Validates FileConnector setting values using custom business logic.
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
        var filePathSettingValue = settingValues.Single(q => q.Setting.Name == SettingExampleFilePath);
        if (!string.IsNullOrEmpty(filePathSettingValue.StringValue) && System.IO.File.Exists(filePathSettingValue.StringValue))
            return response;
        
        // wasn't given a file path, or the path couldn't be accessed, or no file found at the path location. error!
        var connectivityTestResult = new ConnectorSettingValueValidationResult
        {
            IsValid = false,
            ErrorMessage = $"File path not provided, the path couldn't be accessed, or the file doesn't exist. Does '{filePathSettingValue.StringValue}' map to a Docker Volume in the docker-compose.yml file?"
        };
        response.Add(connectivityTestResult);

        return response;
    }
    #endregion

    #region IConnectorSchema members
    /// <summary>
    /// Determine the file schema by inspecting some of the headers and row fields.
    /// </summary>
    public async Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        var exampleFilePath = settingValues.SingleOrDefault(q => q.Setting.Name == SettingExampleFilePath);
        if (exampleFilePath == null || string.IsNullOrEmpty(exampleFilePath.StringValue))
            throw new InvalidSettingValuesException($"Missing setting value for {SettingExampleFilePath}.");

        var objectTypeColumn = settingValues.SingleOrDefault(q => q.Setting.Name == SettingObjectTypeColumn);
        var objectType = settingValues.SingleOrDefault(q => q.Setting.Name == SettingObjectType);
        if ((objectType == null || string.IsNullOrEmpty(objectType.StringValue)) && (objectTypeColumn == null || string.IsNullOrEmpty(objectTypeColumn.StringValue)))
            throw new InvalidSettingValuesException($"Either a {SettingObjectTypeColumn} or {SettingObjectType} need a setting value specifying.");

        var reader = GetCsvReader(exampleFilePath.StringValue, settingValues, logger);
        await reader.CsvReader.ReadAsync();
        reader.CsvReader.ReadHeader();
        var columnNames = reader.CsvReader.HeaderRecord;
        if (columnNames == null || columnNames.Length == 0)
            throw new InvalidOperationException("CSV file is missing column headers.");

        // start building the schema by inspecting the file!
        var schema = new ConnectorSchema();

        var objectTypeInfo = GetFileConnectorObjectTypeInfo(settingValues, logger);
        switch (objectTypeInfo.Specifier)
        {
            case FileConnectorObjectTypeSpecifier.PredefinedObjectType when !string.IsNullOrEmpty(objectTypeInfo.PredefinedObjectType):
            {
                var schemaObjectType = new ConnectorSchemaObjectType(objectTypeInfo.PredefinedObjectType);
                schema.ObjectTypes.Add(schemaObjectType);
                break;
            }
            case FileConnectorObjectTypeSpecifier.ColumnBasedObjectType when !string.IsNullOrEmpty(objectTypeInfo.ObjectTypeColumnName):
            {
                // Read through the file to discover unique object types from the specified column
                var objectTypeColumnName = objectTypeInfo.ObjectTypeColumnName;
                if (!columnNames.Contains(objectTypeColumnName, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Object type column '{objectTypeColumnName}' not found in file headers.");

                var discoveredObjectTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.CsvReader.ReadAsync())
                {
                    var objectTypeName = reader.CsvReader.GetField(objectTypeColumnName);
                    if (!string.IsNullOrEmpty(objectTypeName))
                        discoveredObjectTypes.Add(objectTypeName);
                }

                if (discoveredObjectTypes.Count == 0)
                    throw new InvalidOperationException($"No object types found in column '{objectTypeColumnName}'.");

                foreach (var typeName in discoveredObjectTypes.OrderBy(t => t))
                {
                    var schemaObjectType = new ConnectorSchemaObjectType(typeName);
                    schema.ObjectTypes.Add(schemaObjectType);
                }

                // Reset the reader position for attribute type inference
                reader.Dispose();
                reader = GetCsvReader(exampleFilePath.StringValue, settingValues, logger);
                await reader.CsvReader.ReadAsync();
                reader.CsvReader.ReadHeader();
                break;
            }
            default:
                throw new ArgumentOutOfRangeException();
        }

        // now determine the attributes from the file headers.
        // at this point we don't know what attributes are for what object type. so all object types get the same attributes.
        // later, the user can refine the per-object type attribute lists.
        foreach (var schemaObjectType in schema.ObjectTypes)
        {
            foreach (var columnName in columnNames)
            {
                // has this attribute already been added? if it has, this indicates it's a multivalued attribute
                var existingSchemaAttribute = schemaObjectType.Attributes.SingleOrDefault(q => q.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
                if (existingSchemaAttribute != null)
                {
                    if (existingSchemaAttribute.AttributePlurality != AttributePlurality.MultiValued)
                        existingSchemaAttribute.AttributePlurality = AttributePlurality.MultiValued;

                    continue;
                }

                // initially set the attributes with just a name. we'll work out their data types next.
                schemaObjectType.Attributes.Add(new ConnectorSchemaAttribute(columnName, AttributeDataType.NotSet, AttributePlurality.SingleValued));
            }
        }

        // read some rows and infer the data type of the fields
        const int maxRowsToInspect = 50;
        var rowsInspected = 0;
        while (await reader.CsvReader.ReadAsync())
        {
            if (rowsInspected == maxRowsToInspect)
                break;

            var schemaObjectType = schema.ObjectTypes[0];
            foreach (var schemaAttribute in schemaObjectType.Attributes)
            {
                var field = reader.CsvReader.GetField(schemaAttribute.Name);

                // some fields may be null/empty, skip those and hopefully we'll find
                // a value we can inspect in a later row.
                if (field == null || string.IsNullOrEmpty(field))
                    continue;

                // attempt to infer the data type
                // conflating integers and doubles may turn out to be a bad idea
                if (int.TryParse(field, out _) || double.TryParse(field, out _))
                    schemaAttribute.Type = AttributeDataType.Number;
                else if (bool.TryParse(field, out _))
                    schemaAttribute.Type = AttributeDataType.Boolean;
                else if (Guid.TryParse(field, out _))
                    schemaAttribute.Type = AttributeDataType.Guid;
                else if (DateTime.TryParse(field, out _))
                    schemaAttribute.Type = AttributeDataType.DateTime;
                else
                    schemaAttribute.Type = AttributeDataType.Text;

                // if all fields have a type definition, stop inspecting
                if (schemaObjectType.Attributes.All(a => a.Type != AttributeDataType.NotSet))
                    break;
            }

            rowsInspected++;
        }

        reader.Dispose();
        return schema;
    }
    #endregion

    #region IConnectorImportUsingFiles members
    public async Task<ConnectedSystemImportResult> ImportAsync(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile, ILogger logger, CancellationToken cancellationToken)
    {
        logger.Verbose("ImportAsync() called");

        // todo: see about changing this so it returns a ConnectedSystemImportResult with an error on it
        if (string.IsNullOrEmpty(runProfile.FilePath))
            throw new InvalidDataException($"ImportAsync: FilePath is missing or empty!");

        var reader = GetCsvReader(runProfile.FilePath, connectedSystem.SettingValues, logger);
        var objectTypeInfo = GetFileConnectorObjectTypeInfo(connectedSystem.SettingValues, logger);
        var stopOnFirstError = GetStopOnFirstErrorSetting(connectedSystem.SettingValues);
        var import = new FileConnectorImport(connectedSystem, reader, objectTypeInfo, stopOnFirstError, logger, cancellationToken);
            
        switch (runProfile.RunType)
        {
            case ConnectedSystemRunType.FullImport:
                logger.Debug("ImportAsync: Full Import requested");
                return await import.GetFullImportObjectsAsync();
            case ConnectedSystemRunType.DeltaImport:
                logger.Debug("ImportAsync: Delta Import requested");
                throw new NotSupportedException("Delta Imports are not yet currently supported by this Connector");
            case ConnectedSystemRunType.FullSynchronisation:
            case ConnectedSystemRunType.DeltaSynchronisation:
            case ConnectedSystemRunType.Export:
            default:
                throw new InvalidDataException($"Unsupported import run-type: {runProfile.RunType}");
        }
    }
    #endregion

    #region private methods
    /// <summary>
    /// Helper to simplify opening a file for reading. Takes care of file location and culture specifics.
    /// </summary>
    private static FileConnectorReader GetCsvReader(string filePath, IReadOnlyCollection<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        logger.Verbose("GetCsvReader: Called.");

        // default culture info is invariant culture. hoping this is fine for most data.
        var cultureInfo = CultureInfo.InvariantCulture;

        // though the user can specify a specific culture if they're having problems with characters not being transferred between systems correctly.
        var culture = settingValues.SingleOrDefault(q => q.Setting.Name == SettingCulture);
        if (culture != null && !string.IsNullOrEmpty(culture.StringValue))
            cultureInfo = new CultureInfo(culture.StringValue);

        var delimiter = settingValues.SingleOrDefault(q => q.Setting.Name == SettingDelimiter);
        if (delimiter == null || string.IsNullOrEmpty(delimiter.StringValue))
            throw new InvalidSettingValuesException($"Missing setting value for {SettingDelimiter}.");

        var config = new CsvConfiguration(cultureInfo) { Delimiter = delimiter.StringValue };
        logger.Debug($"GetCsvReader: Attempting to read '{filePath}'.");
        var reader = new StreamReader(filePath);
        var csv = new CsvReader(reader, config);
        return new FileConnectorReader(reader, csv);
    }

    /// <summary>
    /// Helper to make it easy to work out what type of object a row is representing.
    /// </summary>
    private static FileConnectorObjectTypeInfo GetFileConnectorObjectTypeInfo(IReadOnlyCollection<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        logger.Verbose("GetFileConnectorObjectTypeInfo: Called.");
        var objectTypeColumn = settingValues.SingleOrDefault(q => q.Setting.Name == SettingObjectTypeColumn);
        var objectType = settingValues.SingleOrDefault(q => q.Setting.Name == SettingObjectType);
        if ((objectType == null || string.IsNullOrEmpty(objectType.StringValue)) && (objectTypeColumn == null || string.IsNullOrEmpty(objectTypeColumn.StringValue)))
            throw new InvalidSettingValuesException($"Either a {SettingObjectTypeColumn} or {SettingObjectType} need a setting value specifying.");

        var info = new FileConnectorObjectTypeInfo();

        if (objectType != null && !string.IsNullOrEmpty(objectType.StringValue))
        {
            info.Specifier = FileConnectorObjectTypeSpecifier.PredefinedObjectType;
            info.PredefinedObjectType = objectType.StringValue;

        }
        else if (objectTypeColumn != null &&  !string.IsNullOrEmpty(objectTypeColumn.StringValue))
        {
            info.Specifier = FileConnectorObjectTypeSpecifier.ColumnBasedObjectType;
            info.ObjectTypeColumnName = objectTypeColumn.StringValue;
        }
        else
        {
            throw new InvalidDataException("Either a predefined object type, or a column that contains the object type needs to be specified in the settings.");
        }

        return info;
    }

    /// <summary>
    /// Helper to retrieve the "Stop On First Error" setting value.
    /// </summary>
    private static bool GetStopOnFirstErrorSetting(IReadOnlyCollection<ConnectedSystemSettingValue> settingValues)
    {
        var setting = settingValues.SingleOrDefault(q => q.Setting.Name == SettingStopOnFirstError);
        return setting?.CheckboxValue ?? false;
    }
    #endregion
}