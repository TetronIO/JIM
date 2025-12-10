using CsvHelper;
using CsvHelper.Configuration;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;
using System.Globalization;
namespace JIM.Connectors.File;

public class FileConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorSchema, IConnectorImportUsingFiles, IConnectorExportUsingFiles
{
    #region IConnector members
    public string Name => ConnectorConstants.FileConnectorName;

    public string? Description => "Enables bi-directional synchronisation with files, i.e. CSV.";

    public string? Url => "https://github.com/TetronIO/JIM";
    #endregion

    #region IConnectorCapabilities members
    public bool SupportsFullImport => true;
    public bool SupportsDeltaImport => false;
    public bool SupportsExport => true;
    public bool SupportsPartitions => false;
    public bool SupportsPartitionContainers => false;
    public bool SupportsSecondaryExternalId => false;
    public bool SupportsUserSelectedExternalId => true;
    public bool SupportsUserSelectedAttributeTypes => true;
    public bool SupportsAutoConfirmExport => true;
    #endregion

    #region IConnectorSettings members
    // using member variables for the names to reduce repetition later on, i.e. when we go to consume setting values JIM passes in, or when validating administrator-supplied settings
    private const string SettingFilePath = "File Path";
    private const string SettingMode = "Mode";
    private const string SettingObjectTypeColumn = "Object Type Column";
    private const string SettingObjectType = "Object Type";
    private const string SettingCulture = "Culture";
    private const string SettingDelimiter = "Delimiter";
    private const string SettingStopOnFirstError = "Stop On First Error";
    private const string SettingMultiValueDelimiter = "Multi-Value Delimiter";
    private const string SettingIncludeFullState = "Include Full State";

    // Mode values
    private const string ModeImportOnly = "Import Only";
    private const string ModeExportOnly = "Export Only";
    private const string ModeBidirectional = "Bidirectional";

    public List<ConnectorSetting> GetSettings()
    {
        return new List<ConnectorSetting>
        {
            // Primary settings
            new() { Name = SettingFilePath, Required = true, Description = "Path to the CSV file. Used for import, export, or both depending on mode. e.g. /var/connector-files/Users.csv", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.File },
            new()
            {
                Name = SettingMode,
                Required = true,
                Description = "Import Only: Read from file. Export Only: Write to file (auto-confirmed). Bidirectional: Export then confirming import from same file.",
                Category = ConnectedSystemSettingCategory.General,
                Type = ConnectedSystemSettingType.DropDown,
                DropDownValues = new List<string> { ModeImportOnly, ModeExportOnly, ModeBidirectional },
                DefaultStringValue = ModeImportOnly
            },

            // Object type settings
            new() { Name = SettingObjectTypeColumn, Required = false, Description = "Optionally specify the column that contains the object type.", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.String },
            new() { Name = SettingObjectType, Required = false, Description = "Optional: Specify an object type for when the file only contains one type of object, e.g. user.", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.String },

            // Import-specific settings
            new() { Name = SettingStopOnFirstError, Required = false, Description = "Stop processing the file when the first error is encountered. Useful for debugging data quality issues without generating large numbers of errors.", Category = ConnectedSystemSettingCategory.Import, Type = ConnectedSystemSettingType.CheckBox },

            // Export-specific settings
            new() { Name = SettingIncludeFullState, Required = false, Description = "Include all attribute values in exports, not just changed attributes.", Category = ConnectedSystemSettingCategory.Export, Type = ConnectedSystemSettingType.CheckBox },

            // Format settings
            new() { Name = SettingDelimiter, Required = false, Description = "What character to use as the delimiter?", DefaultStringValue = ",", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.String },
            new() { Name = SettingCulture, Required = false, Description = "Optionally specify a culture (i.e. en-gb) for the file contents. Use if you experience problems with the default (invariant culture).", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.String },
            new() { Name = SettingMultiValueDelimiter, Required = false, Description = "Character used to separate multiple values within a single field. Defaults to pipe (|).", DefaultStringValue = "|", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.String }
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
            if ((requiredSettingValue.Setting.Type == ConnectedSystemSettingType.String ||
                 requiredSettingValue.Setting.Type == ConnectedSystemSettingType.File ||
                 requiredSettingValue.Setting.Type == ConnectedSystemSettingType.DropDown) &&
                string.IsNullOrEmpty(requiredSettingValue.StringValue))
                response.Add(new ConnectorSettingValueValidationResult { ErrorMessage = $"Please supply a value for {requiredSettingValue.Setting.Name}", IsValid = false, SettingValue = requiredSettingValue });
        }

        // Get file path and mode settings
        var filePathSetting = settingValues.Single(q => q.Setting.Name == SettingFilePath);
        var modeSetting = settingValues.Single(q => q.Setting.Name == SettingMode);

        var filePath = filePathSetting.StringValue;
        var mode = modeSetting.StringValue ?? ModeImportOnly;

        // File path is required
        if (string.IsNullOrEmpty(filePath))
        {
            response.Add(new ConnectorSettingValueValidationResult
            {
                IsValid = false,
                ErrorMessage = "File Path must be configured.",
                SettingValue = filePathSetting
            });
            return response;
        }

        // Validate based on mode
        switch (mode)
        {
            case ModeImportOnly:
            case ModeBidirectional:
                // For import modes, file must exist
                if (!System.IO.File.Exists(filePath))
                {
                    response.Add(new ConnectorSettingValueValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"File not found: '{filePath}'. For {mode} mode, the file must exist. Check the path and ensure it maps to a Docker Volume.",
                        SettingValue = filePathSetting
                    });
                }
                break;

            case ModeExportOnly:
                // For export only, file doesn't need to exist, but parent directory must exist or be creatable
                if (!System.IO.File.Exists(filePath))
                {
                    var exportDir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(exportDir) && !Directory.Exists(exportDir))
                    {
                        try
                        {
                            Directory.CreateDirectory(exportDir);
                            logger.Information("Created export directory: {ExportDir}", exportDir);
                        }
                        catch (Exception ex)
                        {
                            response.Add(new ConnectorSettingValueValidationResult
                            {
                                IsValid = false,
                                ErrorMessage = $"Export directory could not be created: '{exportDir}'. Error: {ex.Message}",
                                SettingValue = filePathSetting
                            });
                        }
                    }
                }
                break;
        }

        return response;
    }
    #endregion

    #region IConnectorSchema members
    /// <summary>
    /// Determine the file schema by inspecting some of the headers and row fields.
    /// For Export Only mode where file doesn't exist, requires Object Type to be specified.
    /// </summary>
    public async Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        var filePathSetting = settingValues.SingleOrDefault(q => q.Setting.Name == SettingFilePath);
        var modeSetting = settingValues.SingleOrDefault(q => q.Setting.Name == SettingMode);

        var filePath = filePathSetting?.StringValue;
        var mode = modeSetting?.StringValue ?? ModeImportOnly;

        if (string.IsNullOrEmpty(filePath))
        {
            throw new InvalidSettingValuesException($"{SettingFilePath} must be specified for schema discovery.");
        }

        var isExportOnlyMode = mode == ModeExportOnly;
        var fileExists = System.IO.File.Exists(filePath);

        logger.Debug("GetSchemaAsync: Mode={Mode}, FilePath={FilePath}, FileExists={FileExists}", mode, filePath, fileExists);

        // Object type configuration
        var objectTypeColumn = settingValues.SingleOrDefault(q => q.Setting.Name == SettingObjectTypeColumn);
        var objectType = settingValues.SingleOrDefault(q => q.Setting.Name == SettingObjectType);

        // Validate object type configuration based on mode and file existence
        if (isExportOnlyMode && !fileExists)
        {
            // Export only with no existing file - must have Object Type specified
            if (objectType == null || string.IsNullOrEmpty(objectType.StringValue))
                throw new InvalidSettingValuesException($"For Export Only mode without an existing file, {SettingObjectType} must be specified to define the object type for the schema.");

            // Return a schema with just the object type, no attributes (will be defined by sync rules)
            var exportSchema = new ConnectorSchema();
            exportSchema.ObjectTypes.Add(new ConnectorSchemaObjectType(objectType.StringValue));
            logger.Information("GetSchemaAsync: Created empty schema for Export Only mode with object type: {ObjectType}", objectType.StringValue);
            return exportSchema;
        }

        // For modes with an existing file, we can discover schema from the file
        if (!fileExists)
        {
            throw new InvalidSettingValuesException($"File not found: '{filePath}'. For {mode} mode, the file must exist for schema discovery.");
        }

        // Standard schema discovery from file - requires object type configuration
        if ((objectType == null || string.IsNullOrEmpty(objectType.StringValue)) && (objectTypeColumn == null || string.IsNullOrEmpty(objectTypeColumn.StringValue)))
            throw new InvalidSettingValuesException($"Either {SettingObjectTypeColumn} or {SettingObjectType} must be specified for schema discovery.");

        FileConnectorReader? reader = null;
        try
        {
            reader = GetCsvReader(filePath, settingValues, logger);
            await reader.CsvReader.ReadAsync();
            reader.CsvReader.ReadHeader();
            var columnNames = reader.CsvReader.HeaderRecord;
            if (columnNames == null || columnNames.Length == 0)
                throw new CsvParsingException(
                    "CSV file is missing column headers.",
                    rowNumber: null,
                    rawRow: null,
                    columnInfo: null,
                    suggestion: "Ensure the first row of your CSV file contains column headers.");

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
                        throw new CsvParsingException(
                            $"Object type column '{objectTypeColumnName}' not found in file headers.",
                            rowNumber: null,
                            rawRow: null,
                            columnInfo: objectTypeColumnName,
                            suggestion: $"Ensure your CSV file has a column named '{objectTypeColumnName}', or update the 'Object Type Column' setting to match an existing column.");

                    var discoveredObjectTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    while (await reader.CsvReader.ReadAsync())
                    {
                        var objectTypeName = reader.CsvReader.GetField(objectTypeColumnName);
                        if (!string.IsNullOrEmpty(objectTypeName))
                            discoveredObjectTypes.Add(objectTypeName);
                    }

                    if (discoveredObjectTypes.Count == 0)
                        throw new CsvParsingException(
                            $"No object types found in column '{objectTypeColumnName}'.",
                            rowNumber: null,
                            rawRow: null,
                            columnInfo: objectTypeColumnName,
                            suggestion: "Ensure your CSV file has data rows with values in the object type column.");

                    foreach (var typeName in discoveredObjectTypes.OrderBy(t => t))
                    {
                        var schemaObjectType = new ConnectorSchemaObjectType(typeName);
                        schema.ObjectTypes.Add(schemaObjectType);
                    }

                    // Reset the reader position for attribute type inference
                    reader.Dispose();
                    reader = GetCsvReader(filePath, settingValues, logger);
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
            var expectedColumnCount = reader.CsvReader.HeaderRecord?.Length ?? 0;

            while (await reader.CsvReader.ReadAsync())
            {
                if (rowsInspected == maxRowsToInspect)
                    break;

                // Validate that this row has the expected number of columns
                var actualColumnCount = reader.CsvReader.Parser.Count;
                if (actualColumnCount < expectedColumnCount)
                {
                    var rowNumber = reader.CsvReader.Parser.Row;
                    var rawRow = reader.CsvReader.Parser.RawRecord?.Trim();
                    throw new CsvParsingException(
                        $"Row {rowNumber} has {actualColumnCount} columns but the header defines {expectedColumnCount} columns.",
                        rowNumber: rowNumber,
                        rawRow: rawRow,
                        columnInfo: $"Expected {expectedColumnCount} columns, found {actualColumnCount}",
                        suggestion: "Ensure all rows in your CSV file have the same number of columns as the header row. Check for missing commas or values.");
                }

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

            return schema;
        }
        catch (CsvHelper.MissingFieldException ex)
        {
            // CsvHelper throws MissingFieldException when a row has fewer columns than expected
            var context = reader?.CsvReader.Context;
            var rawRow = context?.Parser?.RawRecord?.Trim();
            var rowNumber = context?.Parser?.Row;
            var expectedColumns = context?.Reader?.HeaderRecord?.Length ?? 0;

            throw new CsvParsingException(
                $"Row {rowNumber} has fewer columns than expected. The file header defines {expectedColumns} columns, but this row has fewer values.",
                rowNumber: rowNumber,
                rawRow: rawRow,
                columnInfo: $"Expected {expectedColumns} columns",
                suggestion: "Ensure all rows in your CSV file have the same number of columns as the header row. Check for missing commas or incorrectly quoted fields.",
                innerException: ex);
        }
        catch (BadDataException ex)
        {
            // CsvHelper throws BadDataException for malformed CSV data (e.g., unclosed quotes)
            var context = reader?.CsvReader.Context;
            var rawRow = context?.Parser?.RawRecord?.Trim();
            var rowNumber = context?.Parser?.Row;

            throw new CsvParsingException(
                $"Malformed CSV data at row {rowNumber}. The row contains invalid formatting.",
                rowNumber: rowNumber,
                rawRow: rawRow,
                columnInfo: null,
                suggestion: "Check for unclosed quotation marks, incorrect escaping, or other CSV formatting issues. Fields containing commas or quotes should be enclosed in double quotes.",
                innerException: ex);
        }
        catch (ReaderException ex)
        {
            // General CsvHelper reader exception
            var context = reader?.CsvReader.Context;
            var rawRow = context?.Parser?.RawRecord?.Trim();
            var rowNumber = context?.Parser?.Row;

            throw new CsvParsingException(
                $"Error reading CSV at row {rowNumber}: {ex.Message}",
                rowNumber: rowNumber,
                rawRow: rawRow,
                columnInfo: null,
                suggestion: "Review the CSV file structure. Ensure it follows standard CSV formatting rules.",
                innerException: ex);
        }
        catch (CsvParsingException)
        {
            // Re-throw our own exceptions
            throw;
        }
        catch (InvalidSettingValuesException)
        {
            // Re-throw setting validation exceptions
            throw;
        }
        catch (Exception ex) when (ex is not ArgumentOutOfRangeException)
        {
            // Catch any other unexpected CSV-related errors
            logger.Error(ex, "Unexpected error during CSV schema discovery");
            throw new CsvParsingException(
                $"An unexpected error occurred while reading the CSV file: {ex.Message}",
                rowNumber: null,
                rawRow: null,
                columnInfo: null,
                suggestion: "Check that the file is a valid CSV file and is not corrupted or locked by another application.",
                innerException: ex);
        }
        finally
        {
            reader?.Dispose();
        }
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
        var multiValueDelimiter = GetMultiValueDelimiterSetting(connectedSystem.SettingValues);
        var import = new FileConnectorImport(connectedSystem, reader, objectTypeInfo, stopOnFirstError, multiValueDelimiter, logger, cancellationToken);
            
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

    #region IConnectorExportUsingFiles members
    public void Export(IList<ConnectedSystemSettingValue> settings, IList<PendingExport> pendingExports)
    {
        var logger = Log.ForContext<FileConnector>();
        logger.Verbose("Export() called with {Count} pending exports", pendingExports.Count);

        var export = new FileConnectorExport(settings, pendingExports, logger);
        export.Execute();
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

        var config = new CsvConfiguration(cultureInfo)
        {
            Delimiter = delimiter.StringValue,
            // Throw an exception when a row has fewer fields than the header row
            MissingFieldFound = null
        };
        logger.Debug($"GetCsvReader: Attempting to read '{filePath}'.");
        var reader = new StreamReader(filePath);
        var csv = new CsvReader(reader, config);
        return new FileConnectorReader(reader, csv);
    }

    /// <summary>
    /// Helper to make it easy to work out what type of object a row is representing.
    /// </summary>
    /// <param name="settingValues">The connector setting values.</param>
    /// <param name="logger">The logger.</param>
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
        else if (objectTypeColumn != null && !string.IsNullOrEmpty(objectTypeColumn.StringValue))
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

    /// <summary>
    /// Helper to retrieve the "Multi-Value Delimiter" setting value.
    /// </summary>
    private static string GetMultiValueDelimiterSetting(IReadOnlyCollection<ConnectedSystemSettingValue> settingValues)
    {
        var setting = settingValues.SingleOrDefault(q => q.Setting.Name == SettingMultiValueDelimiter);
        return !string.IsNullOrEmpty(setting?.StringValue) ? setting.StringValue : "|";
    }
    #endregion
}