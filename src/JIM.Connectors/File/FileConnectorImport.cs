// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.Diagnostics;
namespace JIM.Connectors.File;

internal class FileConnectorImport
{
    /// <summary>
    /// How often to report progress while reading the file. A file import is a single call that
    /// returns only once the whole file is read, so this is the only movement an operator sees; the
    /// interval is coarse enough that the Activity write cost stays negligible even for a million rows.
    /// </summary>
    private const int ProgressObjectInterval = 10_000;

    private readonly CancellationToken _cancellationToken;
    private readonly ConnectedSystem _connectedSystem;
    private readonly FileConnectorReader _reader;
    private readonly FileConnectorObjectTypeInfo _objectTypeInfo;
    private readonly bool _stopOnFirstError;
    private readonly string _multiValueDelimiter;
    private readonly ILogger _logger;
    private readonly IConnectorProgress _progress;

    internal FileConnectorImport(
        ConnectedSystem connectedSystem,
        FileConnectorReader reader,
        FileConnectorObjectTypeInfo objectTypeInfo,
        bool stopOnFirstError,
        string multiValueDelimiter,
        ILogger logger,
        CancellationToken cancellationToken,
        IConnectorProgress progress)
    {
        _connectedSystem = connectedSystem;
        _reader = reader;
        _objectTypeInfo = objectTypeInfo;
        _stopOnFirstError = stopOnFirstError;
        _multiValueDelimiter = multiValueDelimiter;
        _logger = logger;
        _cancellationToken = cancellationToken;
        _progress = progress;
    }

    /// <summary>
    /// Checks the file's header row against the schema before any row is read (#2051). Columns name their attributes
    /// ignoring case, so two columns differing only in case cannot be told apart, and a column the schema reads (a
    /// selected attribute, or an External ID or Secondary External ID) that the file lacks would otherwise be read as no
    /// value and clear the attribute on every object. Either fails the import, naming the columns.
    /// </summary>
    private void ValidateHeader(string[] header)
    {
        var clashes = header
            .GroupBy(column => IdentifierKey.For(column, caseExact: false), StringComparer.Ordinal)
            .Where(group => group.Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => string.Join(", ", group.Distinct(StringComparer.Ordinal).Select(column => $"'{column}'")))
            .ToList();
        if (clashes.Count > 0)
            throw new InvalidDataException(
                $"The file '{_reader.FileName}' has columns whose names differ only in case ({string.Join("; ", clashes)}). " +
                "Columns are matched to attributes ignoring case, so JIM cannot tell which to read. Rename or remove one of each.");

        var columns = header.ToHashSet(IdentifierKey.Comparer(caseExact: false));
        var objectTypes = _connectedSystem.ObjectTypes!
            .Where(objectType => objectType.Selected &&
                                 (string.IsNullOrEmpty(_objectTypeInfo.PredefinedObjectType) ||
                                  objectType.Name.Equals(_objectTypeInfo.PredefinedObjectType, StringComparison.OrdinalIgnoreCase)));
        var missing = objectTypes
            .SelectMany(objectType => objectType.Attributes
                .Where(attribute => attribute.Selected || attribute.IsExternalId || attribute.IsSecondaryExternalId)
                .Select(attribute => attribute.Name))
            .Where(name => !columns.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count > 0)
            throw new InvalidDataException(
                $"The file '{_reader.FileName}' has no column for {string.Join(", ", missing.Select(name => $"'{name}'"))}, which the schema reads. " +
                "Add the column, or refresh the schema and deselect the attribute.");
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
        ValidateHeader(_reader.CsvReader.HeaderRecord ?? []);

        var rowsRead = 0;
        while (await _reader.CsvReader.ReadAsync())
        {
            // always check to see if this task has been cancelled by the user.
            if (_cancellationToken.IsCancellationRequested)
            {
                _logger.Information("GetFullImportObjects: O2 Cancellation requested. Stopping.");
                return result;
            }

            rowsRead++;

            // start building the object that we pass back to JIM, representing the Connected System Object.
            // Use NotSet for Full Imports - JIM will determine Create vs Update based on CSO existence.
            // Only delta imports with change tracking should specify explicit Create/Update/Delete.
            var importObject = new ConnectedSystemImportObject { ChangeType = Models.Enums.ObjectChangeType.NotSet };

            // work out what object type this row is meant to be
            if (!string.IsNullOrEmpty(_objectTypeInfo.PredefinedObjectType))
                importObject.ObjectType = _objectTypeInfo.PredefinedObjectType;
            else if (!string.IsNullOrEmpty(_objectTypeInfo.ObjectTypeColumnName))
                importObject.ObjectType = _reader.CsvReader.GetField(_objectTypeInfo.ObjectTypeColumnName);

            // get the schema object type, so we know what attributes we want to pull from the file.
            var objectType = _connectedSystem.ObjectTypes.SingleOrDefault(ot => ot.Name.Equals(importObject.ObjectType, StringComparison.OrdinalIgnoreCase));
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

                // Every value of the attribute that will not parse, so each is reported rather than the first (#2066).
                // Allocated only for the rare attribute that has one.
                List<string>? unreadableValues = null;

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
                                        (unreadableValues ??= []).Add(value);
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
                                        (unreadableValues ??= []).Add(value);
                                }
                            }
                            else
                            {
                                importObjectAttribute.LongValues.Add(_reader.CsvReader.GetField<long>(attribute.Name));
                            }
                        }
                    }
                    else if (attribute.Type == AttributeDataType.Decimal)
                    {
                        // Parse the raw string via DecimalAttributeValue.TryParse rather than CsvHelper's
                        // GetField<decimal>: CsvHelper's converter rejects exponent notation (e.g. "1.5E3"),
                        // which JIM accepts for Decimal attributes. TryParse also returns false on values
                        // outside the range of decimal, so overflow surfaces as this row's import error
                        // rather than being silently truncated or rounded.
                        var fieldValue = _reader.CsvReader.GetField(attribute.Name);
                        if (!string.IsNullOrEmpty(fieldValue))
                        {
                            if (isMultiValued)
                            {
                                var values = fieldValue.Split(_multiValueDelimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                                foreach (var value in values)
                                {
                                    if (DecimalAttributeValue.TryParse(value, out var decimalValue))
                                        importObjectAttribute.DecimalValues.Add(decimalValue);
                                    else
                                        (unreadableValues ??= []).Add(value);
                                }
                            }
                            else
                            {
                                if (DecimalAttributeValue.TryParse(fieldValue, out var decimalValue))
                                    importObjectAttribute.DecimalValues.Add(decimalValue);
                                else
                                    (unreadableValues ??= []).Add(fieldValue);
                            }
                        }
                    }
                    else if (attribute.Type == AttributeDataType.DateTime)
                    {
                        var fieldValue = _reader.CsvReader.GetField(attribute.Name);
                        if (!string.IsNullOrEmpty(fieldValue))
                        {
                            importObjectAttribute.DateTimeValues.Add(_reader.CsvReader.GetField<DateTime>(attribute.Name));
                        }
                    }
                    else if (attribute.Type == AttributeDataType.Boolean)
                    {
                        var fieldValue = _reader.CsvReader.GetField(attribute.Name);
                        if (!string.IsNullOrEmpty(fieldValue))
                        {
                            importObjectAttribute.BoolValues.Add(_reader.CsvReader.GetField<bool>(attribute.Name));
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
                                    (unreadableValues ??= []).Add(value);
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
                    // A single-valued field the typed reader would not convert. Reported with the others below; the
                    // exception is kept at Debug for whoever needs the parser's own account of it.
                    _logger.Debug(ex, "Typed conversion of attribute '{AttributeName}' failed", attribute.Name);
                    (unreadableValues ??= []).Add(_reader.CsvReader.GetField(attribute.Name) ?? string.Empty);
                }

                if (unreadableValues != null)
                {
                    // Report every value that would not parse, and leave the attribute out: what did parse of a
                    // multi-valued attribute is only part of what the file holds, and JIM leaves an attribute with an
                    // unreadable value as it already held it rather than reading it as the whole set (#2066). The object
                    // is still returned, so every other attribute imports.
                    importObject.SourceLocation ??= $"{_reader.FileName}, row {_reader.CsvReader.Context.Parser?.Row ?? 0:N0}";
                    foreach (var unreadableValue in unreadableValues)
                    {
                        _logger.Warning("Failed to parse attribute '{AttributeName}' as {AttributeType} at row {Row}. Raw value: '{RawValue}'",
                            attribute.Name, attribute.Type, _reader.CsvReader.Context.Parser?.Row ?? 0, LogSanitiser.Sanitise(unreadableValue));
                        importObject.AddAttributeValueError(attribute.Name, unreadableValue, UnreadableValueReasons.For(attribute.Type, unreadableValue));
                    }

                    if (_stopOnFirstError)
                    {
                        _logger.Information("GetFullImportObjects: Stop on first error enabled. Stopping after attribute parse error.");
                        result.ImportObjects.Add(importObject);
                        return FinaliseResult(result, stopwatch);
                    }

                    continue;
                }

                importObject.Attributes.Add(importObjectAttribute);
            }

            result.ImportObjects.Add(importObject);

            // Paced on objects rather than rows: a row for an object type that is not selected is
            // read and then skipped, so the two are not always the same number, and it is the
            // objects that the Activity's counters are showing.
            if (result.ImportObjects.Count % ProgressObjectInterval == 0)
                await _progress.ReportObjectsReadAsync(result.ImportObjects.Count);
        }

        // Land on the true total so the last thing an operator sees is what was actually read,
        // rather than the last interval multiple. Skipped when the interval emit already reported it.
        if (result.ImportObjects.Count > 0 && result.ImportObjects.Count % ProgressObjectInterval != 0)
            await _progress.ReportObjectsReadAsync(result.ImportObjects.Count);

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