using JIM.Models.Core;
using JIM.Models.Staging;
using Serilog;
using System.Diagnostics;
namespace JIM.Connectors.File;

internal class FileConnectorImport
{
    private readonly CancellationToken _cancellationToken;
    private readonly ConnectedSystem _connectedSystem;
    private readonly FileConnectorReader _reader;
    private readonly FileConnectorObjectTypeInfo _objectTypeInfo;
    private readonly bool _stopOnFirstError;
    private readonly string _multiValueDelimiter;
    private readonly ILogger _logger;

    internal FileConnectorImport(
        ConnectedSystem connectedSystem,
        FileConnectorReader reader,
        FileConnectorObjectTypeInfo objectTypeInfo,
        bool stopOnFirstError,
        string multiValueDelimiter,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        _connectedSystem = connectedSystem;
        _reader = reader;
        _objectTypeInfo = objectTypeInfo;
        _stopOnFirstError = stopOnFirstError;
        _multiValueDelimiter = multiValueDelimiter;
        _logger = logger;
        _cancellationToken = cancellationToken;
    }

    internal async Task<ConnectedSystemImportResult> GetFullImportObjectsAsync()
    {
        _logger.Verbose("GetFullImportObjects() called");
        var stopwatch = Stopwatch.StartNew();

        var result = new ConnectedSystemImportResult();
        if (_cancellationToken.IsCancellationRequested)
        {
            _logger.Information("GetFullImportObjects: O1 Cancellation requested. Stopping.");
            return result;
        }

        if (_connectedSystem.ObjectTypes == null)
            throw new InvalidDataException("_connectedSystem.ObjectTypes is null!");

        await _reader.CsvReader.ReadAsync();
        _reader.CsvReader.ReadHeader();

        while (await _reader.CsvReader.ReadAsync())
        {
            // always check to see if this task has been cancelled by the user.
            if (_cancellationToken.IsCancellationRequested)
            {
                _logger.Information("GetFullImportObjects: O2 Cancellation requested. Stopping.");
                return result;
            }

            // start building the object that we pass back to JIM, representing the connected system object.
            // Use NotSet for Full Imports - JIM will determine Create vs Update based on CSO existence.
            // Only delta imports with change tracking should specify explicit Create/Update/Delete.
            var importObject = new ConnectedSystemImportObject { ChangeType = Models.Enums.ObjectChangeType.NotSet };

            // work out what object type this row is meant to be
            if (!string.IsNullOrEmpty(_objectTypeInfo.PredefinedObjectType))
                importObject.ObjectType = _objectTypeInfo.PredefinedObjectType;
            else if (!string.IsNullOrEmpty(_objectTypeInfo.ObjectTypeColumnName))
                importObject.ObjectType = _reader.CsvReader.GetField(_objectTypeInfo.ObjectTypeColumnName);

            // get the schema object type, so we know what attributes we want to pull from the file.
            var objectType = _connectedSystem.ObjectTypes.SingleOrDefault(ot => ot.Name.Equals(importObject.ObjectType, StringComparison.InvariantCultureIgnoreCase));
            if (objectType == null)
            {
                importObject.ErrorType = ConnectedSystemImportObjectError.CouldNotDetermineObjectType;
                importObject.ErrorMessage = $"GetFullImportObjects: Couldn't match object type '{importObject.ObjectType}' to the one(s) selected in the schema.";
                result.ImportObjects.Add(importObject);

                if (_stopOnFirstError)
                {
                    _logger.Information("GetFullImportObjects: Stop on first error enabled. Stopping after object type error.");
                    return FinaliseResult(result, stopwatch);
                }

                continue;
            }

            if (!objectType.Selected)
            {
                _logger.Debug($"Object type '{objectType.Name}' specified in the file is not selected. Skipping.");
                continue;
            }

            // enumerate the schema and extract the field from the csv row
            // include selected attributes, plus external ID and secondary external ID attributes
            // to ensure identity and export confirmation attributes are always imported
            foreach (var attribute in objectType.Attributes.Where(q => q.Selected || q.IsExternalId || q.IsSecondaryExternalId).DistinctBy(a => a.Name))
            {
                var importObjectAttribute = new ConnectedSystemImportObjectAttribute
                {
                    Name = attribute.Name,
                    Type = attribute.Type
                };

                var isMultiValued = attribute.AttributePlurality == AttributePlurality.MultiValued;

                try
                {
                    if (attribute.Type == AttributeDataType.Text)
                    {
                        var stringValue = _reader.CsvReader.GetField(attribute.Name);
                        if (!string.IsNullOrEmpty(stringValue))
                        {
                            if (isMultiValued)
                            {
                                // Split by configured delimiter for multi-valued text attributes
                                var values = stringValue.Split(_multiValueDelimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                                importObjectAttribute.StringValues.AddRange(values);
                            }
                            else
                            {
                                importObjectAttribute.StringValues.Add(stringValue);
                            }
                        }
                    }
                    else if (attribute.Type == AttributeDataType.Number)
                    {
                        var fieldValue = _reader.CsvReader.GetField(attribute.Name);
                        if (!string.IsNullOrEmpty(fieldValue))
                        {
                            if (isMultiValued)
                            {
                                var values = fieldValue.Split(_multiValueDelimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                                foreach (var value in values)
                                {
                                    if (int.TryParse(value, out var intValue))
                                        importObjectAttribute.IntValues.Add(intValue);
                                    else
                                        throw new FormatException($"Cannot parse '{value}' as integer");
                                }
                            }
                            else
                            {
                                importObjectAttribute.IntValues.Add(_reader.CsvReader.GetField<int>(attribute.Name));
                            }
                        }
                    }
                    else if (attribute.Type == AttributeDataType.LongNumber)
                    {
                        var fieldValue = _reader.CsvReader.GetField(attribute.Name);
                        if (!string.IsNullOrEmpty(fieldValue))
                        {
                            if (isMultiValued)
                            {
                                var values = fieldValue.Split(_multiValueDelimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                                foreach (var value in values)
                                {
                                    if (long.TryParse(value, out var longValue))
                                        importObjectAttribute.LongValues.Add(longValue);
                                    else
                                        throw new FormatException($"Cannot parse '{value}' as long number");
                                }
                            }
                            else
                            {
                                importObjectAttribute.LongValues.Add(_reader.CsvReader.GetField<long>(attribute.Name));
                            }
                        }
                    }
                    else if (attribute.Type == AttributeDataType.DateTime)
                    {
                        var fieldValue = _reader.CsvReader.GetField(attribute.Name);
                        if (!string.IsNullOrEmpty(fieldValue))
                        {
                            importObjectAttribute.DateTimeValue = _reader.CsvReader.GetField<DateTime>(attribute.Name);
                        }
                    }
                    else if (attribute.Type == AttributeDataType.Boolean)
                    {
                        var fieldValue = _reader.CsvReader.GetField(attribute.Name);
                        if (!string.IsNullOrEmpty(fieldValue))
                        {
                            importObjectAttribute.BoolValue = _reader.CsvReader.GetField<bool>(attribute.Name);
                        }
                    }
                    else if (attribute.Type == AttributeDataType.Guid)
                    {
                        var fieldValue = _reader.CsvReader.GetField(attribute.Name);
                        if (isMultiValued && !string.IsNullOrEmpty(fieldValue))
                        {
                            var values = fieldValue.Split(_multiValueDelimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            foreach (var value in values)
                            {
                                if (Guid.TryParse(value, out var guidValue))
                                    importObjectAttribute.GuidValues.Add(guidValue);
                                else
                                    throw new FormatException($"Cannot parse '{value}' as GUID");
                            }
                        }
                        else if (!string.IsNullOrEmpty(fieldValue))
                        {
                            importObjectAttribute.GuidValues.Add(_reader.CsvReader.GetField<Guid>(attribute.Name));
                        }
                    }
                    else if (attribute.Type == AttributeDataType.Reference)
                    {
                        var referenceValue = _reader.CsvReader.GetField(attribute.Name);
                        if (!string.IsNullOrEmpty(referenceValue))
                        {
                            if (isMultiValued)
                            {
                                var values = referenceValue.Split(_multiValueDelimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                                importObjectAttribute.ReferenceValues.AddRange(values);
                            }
                            else
                            {
                                importObjectAttribute.ReferenceValues.Add(referenceValue);
                            }
                        }
                    }
                    else
                    {
                        throw new NotSupportedException($"FileConnector does not support attribute data type '{attribute.Type}'.");
                    }
                }
                catch (Exception ex) when (ex is not NotSupportedException)
                {
                    // Record the error but continue processing other attributes and rows (unless stopOnFirstError is enabled)
                    var rowNumber = _reader.CsvReader.Context.Parser?.Row ?? 0;
                    var rawValue = _reader.CsvReader.GetField(attribute.Name);
                    _logger.Warning(ex, "Failed to parse attribute '{AttributeName}' as {AttributeType} at row {Row}. Raw value: '{RawValue}'",
                        attribute.Name, attribute.Type, rowNumber, rawValue);

                    importObject.ErrorType = ConnectedSystemImportObjectError.AttributeValueError;
                    importObject.ErrorMessage = $"Failed to parse '{attribute.Name}' as {attribute.Type}: {ex.Message}";

                    if (_stopOnFirstError)
                    {
                        _logger.Information("GetFullImportObjects: Stop on first error enabled. Stopping after attribute parse error.");
                        result.ImportObjects.Add(importObject);
                        return FinaliseResult(result, stopwatch);
                    }

                    // Continue processing - the attribute will be skipped but the object can still be imported with other attributes
                    continue;
                }

                importObject.Attributes.Add(importObjectAttribute);
            }

            result.ImportObjects.Add(importObject);
        }

        return FinaliseResult(result, stopwatch);
    }

    /// <summary>
    /// Helper to cleanly finalise the import result, stopping the timer and disposing the reader.
    /// </summary>
    private ConnectedSystemImportResult FinaliseResult(ConnectedSystemImportResult result, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        _reader.Dispose();
        _logger.Debug($"GetFullImportObjects: Executed in {stopwatch.Elapsed}");
        return result;
    }
}