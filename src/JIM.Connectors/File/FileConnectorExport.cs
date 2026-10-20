// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using CsvHelper;
using CsvHelper.Configuration;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;
using System.Globalization;
namespace JIM.Connectors.File;

/// <summary>
/// Handles CSV export functionality for the FileConnector.
/// Exports use a full-state model: the file always represents the complete current state
/// of all exported objects. Each export run reads the existing file, merges changes,
/// and writes the result. Creates add rows, Updates modify rows, Deletes remove rows.
/// </summary>
internal class FileConnectorExport
{
    private readonly IList<ConnectedSystemSettingValue> _settings;
    private readonly IList<PendingExport> _pendingExports;
    private readonly ILogger _logger;
    private readonly IConnectorProgress _progress;

    internal FileConnectorExport(
        IList<ConnectedSystemSettingValue> settings,
        IList<PendingExport> pendingExports,
        ILogger logger,
        IConnectorProgress progress)
    {
        _settings = settings;
        _pendingExports = pendingExports;
        _logger = logger;
        _progress = progress;
    }

    internal async Task<List<ConnectedSystemExportResult>> ExecuteAsync()
    {
        _logger.Debug("FileConnectorExport.Execute: Starting export of {Count} Pending Exports", _pendingExports.Count);

        if (_pendingExports.Count == 0)
        {
            _logger.Information("FileConnectorExport.Execute: No Pending Exports to process");
            return new List<ConnectedSystemExportResult>();
        }

        var exportFilePath = GetSettingValue("File Path");
        if (string.IsNullOrEmpty(exportFilePath))
            throw new InvalidSettingValuesException("File Path setting is required for export operations.");

        var delimiter = GetSettingValue("Delimiter") is { Length: > 0 } configuredDelimiter ? configuredDelimiter : FileConnector.DefaultDelimiter;
        var multiValueDelimiter = GetSettingValue("Multi-Value Delimiter") is { Length: > 0 } configuredMultiValueDelimiter ? configuredMultiValueDelimiter : "|";

        // Ensure the export directory exists
        var exportDir = Path.GetDirectoryName(exportFilePath);
        if (!string.IsNullOrEmpty(exportDir) && !Directory.Exists(exportDir))
            Directory.CreateDirectory(exportDir);

        // Determine the External ID attribute name from the Pending Exports' schema metadata
        var externalIdAttributeName = FindExternalIdAttributeName();
        if (string.IsNullOrEmpty(externalIdAttributeName))
        {
            _logger.Error("FileConnectorExport.Execute: Could not determine External ID attribute from Pending Exports. Cannot proceed with full-state export.");
            return _pendingExports.Select(_ => ConnectedSystemExportResult.Failed("No External ID attribute configured for this Connected System.")).ToList();
        }

        _logger.Debug("FileConnectorExport.Execute: Using External ID attribute '{ExternalIdAttribute}'", externalIdAttributeName);

        // Load existing file content first so we can merge its headers with the Pending Exports' attributes.
        // At scale this is the first of three phases that take real wall-clock time, so narrate each one:
        // without it the Activity message sits on "0 of N" for the whole connector call.
        await _progress.EnterPhaseAsync(FileConnectorPhases.LoadExistingFile);
        var existingFileHeaders = new List<string>();
        var existingRows = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var unkeyedRows = new List<Dictionary<string, string>>();

        // The file is rewritten whole, so every row this run does not change must come back as it was. Where JIM cannot
        // be sure of that, every export fails and the file is left untouched, rather than rewritten with only the rows
        // this run touched.
        if (LoadExistingFileContent(exportFilePath, delimiter, multiValueDelimiter, externalIdAttributeName, existingFileHeaders, existingRows, unkeyedRows) is { } fileProblem)
        {
            _logger.Error("FileConnectorExport.Execute: {FileProblem}", LogSanitiser.Sanitise(fileProblem));
            return _pendingExports.Select(_ => ConnectedSystemExportResult.Failed(fileProblem)).ToList();
        }

        // Determine schema columns by merging existing file headers with the Pending Exports' attribute metadata
        var attributeColumns = CollectAttributeColumns(existingFileHeaders, externalIdAttributeName);
        if (attributeColumns.Count == 0)
        {
            _logger.Warning("FileConnectorExport.Execute: No attribute columns found in Pending Exports or existing file");
            return _pendingExports.Select(_ => ConnectedSystemExportResult.Failed("No attributes found in Pending Exports.")).ToList();
        }

        // Process each Pending Export and build results
        await _progress.EnterPhaseAsync(FileConnectorPhases.Merge, $"Merging {_pendingExports.Count:N0} changes into file...");
        var results = new List<ConnectedSystemExportResult>();
        var createdCount = 0;
        var updatedCount = 0;
        var deletedCount = 0;

        foreach (var pendingExport in _pendingExports)
        {
            try
            {
                var result = ProcessPendingExport(pendingExport, existingRows, externalIdAttributeName, multiValueDelimiter);
                results.Add(result);

                if (result.Success)
                {
                    switch (pendingExport.ChangeType)
                    {
                        case PendingExportChangeType.Create:
                            createdCount++;
                            break;
                        case PendingExportChangeType.Update:
                            updatedCount++;
                            break;
                        case PendingExportChangeType.Delete:
                            deletedCount++;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "FileConnectorExport.Execute: Error processing Pending Export {PendingExportId}", pendingExport.Id);
                results.Add(ConnectedSystemExportResult.Failed($"Error processing export: {ex.Message}"));
            }
        }

        // Write the full-state file
        await _progress.EnterPhaseAsync(FileConnectorPhases.Write, $"Writing {existingRows.Count:N0} rows to output file...");
        WriteFullStateFile(exportFilePath, delimiter, attributeColumns, existingRows, unkeyedRows);

        _logger.Information(
            "FileConnectorExport.Execute: Export complete. Created: {Created}, Updated: {Updated}, Deleted: {Deleted}. Total rows in file: {TotalRows}",
            createdCount, updatedCount, deletedCount, existingRows.Count);

        return results;
    }

    /// <summary>
    /// Finds the External ID attribute name from the Pending Exports' attribute metadata.
    /// </summary>
    private string? FindExternalIdAttributeName()
    {
        foreach (var pendingExport in _pendingExports)
        {
            var externalIdAttr = pendingExport.AttributeValueChanges
                .FirstOrDefault(a => a.Attribute?.IsExternalId == true);

            if (externalIdAttr?.Attribute != null)
                return externalIdAttr.Attribute.Name;
        }

        // Fallback: find the External ID attribute name from the CSO's loaded attribute values.
        // Update/Delete exports don't include the External ID in AttributeValueChanges (it hasn't changed),
        // but the CSO's AttributeValues collection is always loaded with Attribute navigations.
        // Use ExternalIdAttributeId (always populated on the CSO) to find the matching attribute.
        foreach (var pendingExport in _pendingExports)
        {
            var cso = pendingExport.ConnectedSystemObject;
            if (cso?.ExternalIdAttributeId is > 0)
            {
                var externalIdAttrValue = cso.AttributeValues
                    .FirstOrDefault(av => av.AttributeId == cso.ExternalIdAttributeId);
                if (externalIdAttrValue?.Attribute != null)
                    return externalIdAttrValue.Attribute.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Collects all attribute column names by merging existing file headers with the
    /// Pending Exports' attribute metadata, in alphabetical order.
    /// This ensures updates and deletes don't lose columns that were in the original file. The External ID column is
    /// always written, even when no change names it, since without it no later export or import can tell rows apart.
    /// </summary>
    private List<string> CollectAttributeColumns(List<string> existingFileHeaders, string externalIdAttributeName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { externalIdAttributeName };

        // Include columns from the existing file
        foreach (var header in existingFileHeaders)
        {
            columns.Add(header);
        }

        // Include columns from the Pending Exports' attribute metadata
        foreach (var export in _pendingExports)
        {
            foreach (var attrChange in export.AttributeValueChanges)
            {
                if (!string.IsNullOrEmpty(attrChange.Attribute?.Name))
                    columns.Add(attrChange.Attribute.Name);
            }
        }

        return columns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Loads the existing file into <paramref name="rows"/>, keyed by External ID, and <paramref name="unkeyedRows"/>,
    /// the rows with no External ID, which are written back unchanged. Populates <paramref name="existingFileHeaders"/>
    /// with the file's column headers. Leaves everything empty when there is no file yet.
    /// </summary>
    /// <returns>
    /// Why the file cannot be rewritten without losing rows this run was not asked to change, or null when it can: it
    /// cannot be read, its header names a column twice (#2186), it has rows but no External ID column, or two rows claim
    /// the same External ID.
    /// </returns>
    private string? LoadExistingFileContent(
        string filePath,
        string delimiter,
        string multiValueDelimiter,
        string externalIdAttributeName,
        List<string> existingFileHeaders,
        Dictionary<string, Dictionary<string, string>> rows,
        List<Dictionary<string, string>> unkeyedRows)
    {
        if (!System.IO.File.Exists(filePath))
        {
            _logger.Debug("FileConnectorExport.LoadExistingFileContent: File does not exist, starting a new one");
            return null;
        }

        var fileName = Path.GetFileName(filePath);
        try
        {
            using var reader = new StreamReader(filePath);
            using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                Delimiter = delimiter,
                HeaderValidated = null,
                MissingFieldFound = null,
                // As on import: a column header names its attribute ignoring case (#2051).
                PrepareHeaderForMatch = args => IdentifierKey.For(args.Header, caseExact: false)
            });

            if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord is not { Length: > 0 } headers)
            {
                _logger.Debug("FileConnectorExport.LoadExistingFileContent: File has no headers");
                return null;
            }

            // A file whose header repeats a column cannot be rewritten without losing every column after the first of
            // that name, for every row (#2186).
            if (FileConnectorHeader.FindProblem(headers, fileName, multiValueDelimiter) is { } headerProblem)
                return headerProblem;

            // Capture headers so the caller can merge them into the attribute columns
            existingFileHeaders.AddRange(headers);

            var hasExternalIdColumn = headers.Contains(externalIdAttributeName, StringComparer.OrdinalIgnoreCase);
            var rowNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            while (csv.Read())
            {
                // Without the External ID column no row can be matched to its object, and rewriting would lose them all.
                if (!hasExternalIdColumn)
                    return $"The file '{fileName}' has rows but no '{externalIdAttributeName}' column, so JIM cannot tell which row " +
                           "belongs to which object, and rewriting it would lose them. Add the column, or move the file aside so JIM " +
                           "starts a new one, then run the export again.";

                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in headers)
                {
                    var value = csv.GetField(header);
                    if (value != null)
                        row[header] = value;
                }

                var externalId = csv.GetField(externalIdAttributeName);
                var rowNumber = csv.Parser.Row;
                if (string.IsNullOrEmpty(externalId))
                {
                    // Not JIM's to change, and not JIM's to drop: written back as it is.
                    _logger.Warning("FileConnectorExport.LoadExistingFileContent: Row {Row} has no External ID; keeping it unchanged", rowNumber);
                    unkeyedRows.Add(row);
                    continue;
                }

                if (rowNumbers.TryGetValue(externalId, out var firstRow))
                    return $"The file '{fileName}' has more than one row for External ID '{externalId}' (rows {firstRow:N0} and " +
                           $"{rowNumber:N0}), so JIM cannot tell which one to update. Remove the duplicate row, then run the export again.";

                rowNumbers[externalId] = rowNumber;
                rows[externalId] = row;
            }

            _logger.Debug("FileConnectorExport.LoadExistingFileContent: Loaded {Count} existing rows from file", rows.Count + unkeyedRows.Count);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CsvHelperException)
        {
            _logger.Warning(ex, "FileConnectorExport.LoadExistingFileContent: Could not read the existing file");
            return $"The file '{fileName}' could not be read ({ex.Message.Split('\n')[0].Trim()}), so JIM cannot rewrite it without losing " +
                   "the rows it holds. Correct or move the file, then run the export again.";
        }
    }

    /// <summary>
    /// Processes a single Pending Export, applying changes to the existing rows dictionary.
    /// Returns an ConnectedSystemExportResult with the External ID for Create operations.
    /// </summary>
    private ConnectedSystemExportResult ProcessPendingExport(
        PendingExport pendingExport,
        Dictionary<string, Dictionary<string, string>> existingRows,
        string externalIdAttributeName,
        string multiValueDelimiter)
    {
        // Determine the External ID value for this export
        var externalId = GetExternalIdValue(pendingExport, externalIdAttributeName, multiValueDelimiter);

        switch (pendingExport.ChangeType)
        {
            case PendingExportChangeType.Create:
            {
                if (string.IsNullOrEmpty(externalId))
                {
                    _logger.Warning("FileConnectorExport.ProcessPendingExport: Create export has no External ID value for attribute '{ExternalIdAttribute}'",
                        externalIdAttributeName);
                    return ConnectedSystemExportResult.Failed($"Create export has no value for External ID attribute '{externalIdAttributeName}'.");
                }

                if (existingRows.ContainsKey(externalId))
                {
                    _logger.Warning("FileConnectorExport.ProcessPendingExport: Create export for '{ExternalId}' but row already exists. Treating as update.",
                        LogSanitiser.Sanitise(externalId));
                }

                // Build the row from attribute changes, then the CSO's full state (for attributes not in changes)
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var problem = ApplyAttributeChanges(row, pendingExport, multiValueDelimiter) ?? ApplyCsoFullState(row, pendingExport, multiValueDelimiter);
                if (problem != null)
                    return ConnectedSystemExportResult.Failed(problem);

                existingRows[externalId] = row;

                return ConnectedSystemExportResult.Succeeded(externalId);
            }

            case PendingExportChangeType.Update:
            {
                if (string.IsNullOrEmpty(externalId))
                {
                    _logger.Warning("FileConnectorExport.ProcessPendingExport: Update export has no External ID value");
                    return ConnectedSystemExportResult.Failed("Update export has no External ID value.");
                }

                if (!existingRows.TryGetValue(externalId, out var existingRow))
                {
                    _logger.Warning("FileConnectorExport.ProcessPendingExport: Update for '{ExternalId}' but no existing row found. Creating new row.",
                        LogSanitiser.Sanitise(externalId));
                    existingRow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                // Changes are applied to a copy, so an export refused part-way leaves the row as the file held it.
                var updatedRow = new Dictionary<string, string>(existingRow, StringComparer.OrdinalIgnoreCase);
                var problem = ApplyAttributeChanges(updatedRow, pendingExport, multiValueDelimiter) ?? ApplyCsoFullState(updatedRow, pendingExport, multiValueDelimiter);
                if (problem != null)
                    return ConnectedSystemExportResult.Failed(problem);

                existingRows[externalId] = updatedRow;

                return ConnectedSystemExportResult.Succeeded(externalId);
            }

            case PendingExportChangeType.Delete:
            {
                if (string.IsNullOrEmpty(externalId))
                {
                    _logger.Warning("FileConnectorExport.ProcessPendingExport: Delete export has no External ID value");
                    return ConnectedSystemExportResult.Failed("Delete export has no External ID value.");
                }

                if (existingRows.Remove(externalId))
                {
                    _logger.Debug("FileConnectorExport.ProcessPendingExport: Removed row for '{ExternalId}'", LogSanitiser.Sanitise(externalId));
                }
                else
                {
                    _logger.Debug("FileConnectorExport.ProcessPendingExport: Delete for '{ExternalId}' but row not found in file (already removed or never exported)",
                        LogSanitiser.Sanitise(externalId));
                }

                return ConnectedSystemExportResult.Succeeded(externalId);
            }

            default:
                return ConnectedSystemExportResult.Failed($"Unsupported change type: {pendingExport.ChangeType}");
        }
    }

    /// <summary>
    /// Gets the External ID value from a Pending Export.
    /// For Updates/Deletes, uses the CSO's External ID attribute value.
    /// For Creates, finds the External ID value in the attribute changes.
    /// </summary>
    private string? GetExternalIdValue(PendingExport pendingExport, string externalIdAttributeName, string multiValueDelimiter)
    {
        // For Updates/Deletes: get from the CSO's existing External ID
        if (pendingExport.ConnectedSystemObject?.ExternalIdAttributeValue != null)
        {
            var value = pendingExport.ConnectedSystemObject.ExternalIdAttributeValue.ToStringNoName();
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        // For Creates (or if CSO didn't have it): find in the attribute changes
        var externalIdChange = pendingExport.AttributeValueChanges
            .FirstOrDefault(a => a.Attribute?.IsExternalId == true
                || (a.Attribute?.Name != null && a.Attribute.Name.Equals(externalIdAttributeName, StringComparison.OrdinalIgnoreCase)));

        if (externalIdChange != null)
            return FormatAttributeValue(externalIdChange, multiValueDelimiter);

        return null;
    }

    /// <summary>
    /// Applies attribute value changes from a Pending Export to a row dictionary, returning why the export cannot be
    /// written, or null once it is applied.
    /// </summary>
    /// <remarks>
    /// A single-valued attribute's change is its whole cell. A multi-valued attribute's changes are each one value
    /// added or removed (on an Update, only the values that changed), so its cell is the set the changes leave behind:
    /// the cell as the file holds it (or, where the file has no row, the Connected System Object's values), less what
    /// is removed, plus what is added (#2186).
    /// </remarks>
    private static string? ApplyAttributeChanges(
        Dictionary<string, string> row,
        PendingExport pendingExport,
        string multiValueDelimiter)
    {
        var changesByAttribute = pendingExport.AttributeValueChanges
            .Where(change => change.Attribute?.Name != null)
            .GroupBy(change => change.Attribute.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var changes in changesByAttribute)
        {
            var attribute = changes.First().Attribute;
            if (attribute.AttributePlurality != AttributePlurality.MultiValued)
            {
                foreach (var attrChange in changes)
                {
                    row[attribute.Name] = attrChange.ChangeType is PendingExportAttributeChangeType.Remove or PendingExportAttributeChangeType.RemoveAll
                        ? string.Empty
                        : FormatAttributeValue(attrChange, multiValueDelimiter);
                }

                continue;
            }

            var values = row.TryGetValue(attribute.Name, out var cell)
                ? SplitCell(cell, attribute, multiValueDelimiter)
                : CsoValues(pendingExport, attribute, multiValueDelimiter);
            var comparer = ValueComparer(attribute);

            if (changes.Any(change => change.ChangeType == PendingExportAttributeChangeType.RemoveAll))
                values.Clear();

            foreach (var removed in changes.Where(change => change.ChangeType == PendingExportAttributeChangeType.Remove))
            {
                var value = Canonicalise(FormatAttributeValue(removed, multiValueDelimiter), attribute.Type);
                values.RemoveAll(held => comparer.Equals(held, value));
            }

            foreach (var added in changes.Where(change => change.ChangeType is PendingExportAttributeChangeType.Add or PendingExportAttributeChangeType.Update))
            {
                var value = Canonicalise(FormatAttributeValue(added, multiValueDelimiter), attribute.Type);
                if (value.Contains(multiValueDelimiter, StringComparison.Ordinal))
                    return DelimiterInValue(attribute.Name, multiValueDelimiter);

                if (value.Length > 0 && !values.Contains(value, comparer))
                    values.Add(value);
            }

            row[attribute.Name] = string.Join(multiValueDelimiter, values);
        }

        return null;
    }

    /// <summary>
    /// Applies full state from the CSO's current attribute values to the row,
    /// for any attributes not already present from the Pending Export changes.
    /// This ensures the file has complete attribute data for each row. Returns why the export cannot be written, or
    /// null once it is applied.
    /// </summary>
    private static string? ApplyCsoFullState(
        Dictionary<string, string> row,
        PendingExport pendingExport,
        string multiValueDelimiter)
    {
        if (pendingExport.ConnectedSystemObject?.AttributeValues == null)
            return null;

        var valuesByAttribute = pendingExport.ConnectedSystemObject.AttributeValues
            .Where(attrValue => attrValue.Attribute?.Name != null)
            .GroupBy(attrValue => attrValue.Attribute.Name, StringComparer.OrdinalIgnoreCase)
            // Don't overwrite values that were explicitly set by the Pending Export changes
            .Where(attrValues => !row.ContainsKey(attrValues.Key));

        foreach (var attrValues in valuesByAttribute)
        {
            var attribute = attrValues.First().Attribute;
            if (attribute.AttributePlurality != AttributePlurality.MultiValued)
            {
                row[attribute.Name] = FormatCsoAttributeValue(attrValues.First(), multiValueDelimiter);
                continue;
            }

            var values = CsoValues(pendingExport, attribute, multiValueDelimiter);
            if (values.Any(value => value.Contains(multiValueDelimiter, StringComparison.Ordinal)))
                return DelimiterInValue(attribute.Name, multiValueDelimiter);

            row[attribute.Name] = string.Join(multiValueDelimiter, values);
        }

        return null;
    }

    /// <summary>
    /// The Connected System Object's values of a multi-valued attribute, in their cell form, without duplicates.
    /// </summary>
    private static List<string> CsoValues(PendingExport pendingExport, ConnectedSystemObjectTypeAttribute attribute, string multiValueDelimiter)
    {
        var values = new List<string>();
        var comparer = ValueComparer(attribute);
        var held = pendingExport.ConnectedSystemObject?.AttributeValues
            .Where(attrValue => attrValue.Attribute?.Name != null && attrValue.Attribute.Name.Equals(attribute.Name, StringComparison.OrdinalIgnoreCase))
            .Select(attrValue => Canonicalise(FormatCsoAttributeValue(attrValue, multiValueDelimiter), attribute.Type))
            ?? [];

        values.AddRange(held.Where(value => value.Length > 0).Distinct(comparer));
        return values;
    }

    /// <summary>
    /// A multi-valued cell's values, each put in the form JIM writes, so a value written by hand in another form
    /// (<c>1.50</c>, a date with no fraction) is still recognised when a change removes or re-adds it. Every value a
    /// multi-valued cell is built from passes through <see cref="Canonicalise"/>, so the set is compared in one form.
    /// </summary>
    private static List<string> SplitCell(string cell, ConnectedSystemObjectTypeAttribute attribute, string multiValueDelimiter) =>
        cell.Split(multiValueDelimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Canonicalise(value, attribute.Type))
            .Distinct(ValueComparer(attribute))
            .ToList();

    private static string Canonicalise(string value, AttributeDataType type) => type switch
    {
        AttributeDataType.Number when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) =>
            number.ToString(CultureInfo.InvariantCulture),
        AttributeDataType.LongNumber when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longNumber) =>
            longNumber.ToString(CultureInfo.InvariantCulture),
        AttributeDataType.Decimal when DecimalAttributeValue.TryParse(value, out var decimalValue) =>
            DecimalAttributeValue.ToCanonicalString(decimalValue),
        AttributeDataType.DateTime when DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dateTime) =>
            dateTime.ToString("O"),
        AttributeDataType.Guid when Guid.TryParse(value, out var guid) => guid.ToString(),
        _ => value
    };

    /// <summary>
    /// How two values of the attribute are told apart: text and references ignoring case unless the attribute is
    /// case-exact, every other type by its written form, which is already canonical.
    /// </summary>
    private static StringComparer ValueComparer(ConnectedSystemObjectTypeAttribute attribute) =>
        attribute.Type is AttributeDataType.Text or AttributeDataType.Reference && !attribute.CaseExact
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static string DelimiterInValue(string attributeName, string multiValueDelimiter) =>
        $"A value of the multi-valued attribute '{attributeName}' contains the Multi-Value Delimiter ({multiValueDelimiter}), " +
        "so the file would read it back as more than one value. Choose a Multi-Value Delimiter that no value contains.";

    /// <summary>
    /// Writes the full-state file from the merged rows dictionary, then the rows with no External ID as they were. The
    /// file is written beside the original and then moved over it, so a write that fails part-way leaves the original
    /// whole rather than truncated.
    /// </summary>
    private void WriteFullStateFile(
        string filePath,
        string delimiter,
        List<string> attributeColumns,
        Dictionary<string, Dictionary<string, string>> rows,
        List<Dictionary<string, string>> unkeyedRows)
    {
        _logger.Debug("FileConnectorExport.WriteFullStateFile: Writing {RowCount} rows to {Path}", rows.Count + unkeyedRows.Count, filePath);

        var temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var writer = new StreamWriter(temporaryPath))
            using (var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = delimiter }))
            {
                // Write header - use attribute columns only (no system columns)
                foreach (var column in attributeColumns)
                    csv.WriteField(column);
                csv.NextRecord();

                // Write data rows
                foreach (var row in rows.Values.Concat(unkeyedRows))
                {
                    foreach (var column in attributeColumns)
                        csv.WriteField(row.TryGetValue(column, out var value) ? value : string.Empty);
                    csv.NextRecord();
                }
            }

            System.IO.File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (System.IO.File.Exists(temporaryPath))
                System.IO.File.Delete(temporaryPath);
        }
    }

    private static string FormatAttributeValue(PendingExportAttributeValueChange attrChange, string multiValueDelimiter)
    {
        if (!string.IsNullOrEmpty(attrChange.StringValue))
            return attrChange.StringValue;

        if (attrChange.IntValue.HasValue)
            return attrChange.IntValue.Value.ToString();

        if (attrChange.LongValue.HasValue)
            return attrChange.LongValue.Value.ToString();

        if (attrChange.DecimalValue.HasValue)
            // Canonical invariant form: plain notation, no trailing zeros, never exponent.
            // A bare ToString() would be culture-sensitive (comma decimal separators would corrupt the CSV).
            return DecimalAttributeValue.ToCanonicalString(attrChange.DecimalValue.Value);

        if (attrChange.DateTimeValue.HasValue)
            return attrChange.DateTimeValue.Value.ToString("O"); // ISO 8601 format

        if (attrChange.GuidValue.HasValue)
            return attrChange.GuidValue.Value.ToString();

        if (attrChange.BoolValue.HasValue)
            return attrChange.BoolValue.Value.ToString().ToLower();

        if (attrChange.ByteValue != null)
            // A CSV cell cannot carry raw bytes; base64 is the lossless text representation.
            return Convert.ToBase64String(attrChange.ByteValue);

        if (!string.IsNullOrEmpty(attrChange.UnresolvedReferenceValue))
            return attrChange.UnresolvedReferenceValue;

        return string.Empty;
    }

    private static string FormatCsoAttributeValue(ConnectedSystemObjectAttributeValue attrValue, string multiValueDelimiter)
    {
        if (!string.IsNullOrEmpty(attrValue.StringValue))
            return attrValue.StringValue;

        if (attrValue.IntValue.HasValue)
            return attrValue.IntValue.Value.ToString();

        if (attrValue.LongValue.HasValue)
            return attrValue.LongValue.Value.ToString();

        if (attrValue.DecimalValue.HasValue)
            // Canonical invariant form: plain notation, no trailing zeros, never exponent.
            // A bare ToString() would be culture-sensitive (comma decimal separators would corrupt the CSV).
            return DecimalAttributeValue.ToCanonicalString(attrValue.DecimalValue.Value);

        if (attrValue.DateTimeValue.HasValue)
            return attrValue.DateTimeValue.Value.ToString("O");

        if (attrValue.GuidValue.HasValue)
            return attrValue.GuidValue.Value.ToString();

        if (attrValue.BoolValue.HasValue)
            return attrValue.BoolValue.Value.ToString().ToLower();

        if (attrValue.ByteValue != null)
            // A CSV cell cannot carry raw bytes; base64 is the lossless text representation.
            return Convert.ToBase64String(attrValue.ByteValue);

        if (!string.IsNullOrEmpty(attrValue.UnresolvedReferenceValue))
            return attrValue.UnresolvedReferenceValue;

        if (attrValue.ReferenceValue != null)
            return attrValue.ReferenceValue.ExternalIdAttributeValue?.StringValue ?? attrValue.ReferenceValue.Id.ToString();

        return string.Empty;
    }

    private string? GetSettingValue(string settingName)
    {
        return _settings.SingleOrDefault(s => s.Setting.Name == settingName)?.StringValue;
    }
}
