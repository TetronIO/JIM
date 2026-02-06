using CsvHelper;
using CsvHelper.Configuration;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
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

    internal FileConnectorExport(
        IList<ConnectedSystemSettingValue> settings,
        IList<PendingExport> pendingExports,
        ILogger logger)
    {
        _settings = settings;
        _pendingExports = pendingExports;
        _logger = logger;
    }

    internal List<ExportResult> Execute()
    {
        _logger.Debug("FileConnectorExport.Execute: Starting export of {Count} pending exports", _pendingExports.Count);

        if (_pendingExports.Count == 0)
        {
            _logger.Information("FileConnectorExport.Execute: No pending exports to process");
            return new List<ExportResult>();
        }

        var exportFilePath = GetSettingValue("File Path");
        if (string.IsNullOrEmpty(exportFilePath))
            throw new InvalidOperationException("File Path setting is required for export operations.");

        var delimiter = GetSettingValue("Delimiter") ?? ",";
        var multiValueDelimiter = GetSettingValue("Multi-Value Delimiter") ?? "|";

        // Ensure the export directory exists
        var exportDir = Path.GetDirectoryName(exportFilePath);
        if (!string.IsNullOrEmpty(exportDir) && !Directory.Exists(exportDir))
            Directory.CreateDirectory(exportDir);

        // Determine the External ID attribute name from the pending exports' schema metadata
        var externalIdAttributeName = FindExternalIdAttributeName();
        if (string.IsNullOrEmpty(externalIdAttributeName))
        {
            _logger.Error("FileConnectorExport.Execute: Could not determine External ID attribute from pending exports. Cannot proceed with full-state export.");
            return _pendingExports.Select(_ => ExportResult.Failed("No External ID attribute configured for this Connected System.")).ToList();
        }

        _logger.Debug("FileConnectorExport.Execute: Using External ID attribute '{ExternalIdAttribute}'", externalIdAttributeName);

        // Load existing file content first so we can merge its headers with the pending exports' attributes
        var existingFileHeaders = new List<string>();
        var existingRows = LoadExistingFileContent(exportFilePath, delimiter, externalIdAttributeName, existingFileHeaders);

        // Determine schema columns by merging existing file headers with the pending exports' attribute metadata
        var attributeColumns = CollectAttributeColumns(existingFileHeaders);
        if (attributeColumns.Count == 0)
        {
            _logger.Warning("FileConnectorExport.Execute: No attribute columns found in pending exports or existing file");
            return _pendingExports.Select(_ => ExportResult.Failed("No attributes found in pending exports.")).ToList();
        }

        // Process each pending export and build results
        var results = new List<ExportResult>();
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
                _logger.Error(ex, "FileConnectorExport.Execute: Error processing pending export {PendingExportId}", pendingExport.Id);
                results.Add(ExportResult.Failed($"Error processing export: {ex.Message}"));
            }
        }

        // Write the full-state file
        WriteFullStateFile(exportFilePath, delimiter, attributeColumns, existingRows);

        _logger.Information(
            "FileConnectorExport.Execute: Export complete. Created: {Created}, Updated: {Updated}, Deleted: {Deleted}. Total rows in file: {TotalRows}",
            createdCount, updatedCount, deletedCount, existingRows.Count);

        return results;
    }

    /// <summary>
    /// Finds the External ID attribute name from the pending exports' attribute metadata.
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

        // Fallback: check the CSO for existing exports (Update/Delete)
        foreach (var pendingExport in _pendingExports)
        {
            if (pendingExport.ConnectedSystemObject?.Type?.Attributes != null)
            {
                var externalIdAttr = pendingExport.ConnectedSystemObject.Type.Attributes
                    .FirstOrDefault(a => a.IsExternalId);
                if (externalIdAttr != null)
                    return externalIdAttr.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Collects all attribute column names by merging existing file headers with the
    /// pending exports' attribute metadata, in alphabetical order.
    /// This ensures updates and deletes don't lose columns that were in the original file.
    /// </summary>
    private List<string> CollectAttributeColumns(List<string> existingFileHeaders)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Include columns from the existing file
        foreach (var header in existingFileHeaders)
        {
            columns.Add(header);
        }

        // Include columns from the pending exports' attribute metadata
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
    /// Loads existing file content into a dictionary keyed by External ID.
    /// Populates existingFileHeaders with the column headers from the file.
    /// Returns an empty dictionary if the file doesn't exist or has no data rows.
    /// </summary>
    private Dictionary<string, Dictionary<string, string>> LoadExistingFileContent(
        string filePath, string delimiter, string externalIdAttributeName, List<string> existingFileHeaders)
    {
        var rows = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        if (!System.IO.File.Exists(filePath))
        {
            _logger.Debug("FileConnectorExport.LoadExistingFileContent: File does not exist, starting fresh");
            return rows;
        }

        try
        {
            using var reader = new StreamReader(filePath);
            using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                Delimiter = delimiter,
                HeaderValidated = null,
                MissingFieldFound = null
            });

            csv.Read();
            csv.ReadHeader();

            var headers = csv.HeaderRecord;
            if (headers == null || headers.Length == 0)
            {
                _logger.Debug("FileConnectorExport.LoadExistingFileContent: File has no headers");
                return rows;
            }

            // Capture headers so the caller can merge them into the attribute columns
            existingFileHeaders.AddRange(headers);

            // Check if the External ID column exists in the file
            if (!headers.Contains(externalIdAttributeName, StringComparer.OrdinalIgnoreCase))
            {
                _logger.Warning(
                    "FileConnectorExport.LoadExistingFileContent: External ID column '{ExternalIdColumn}' not found in existing file headers. Starting fresh.",
                    externalIdAttributeName);
                return rows;
            }

            while (csv.Read())
            {
                var externalId = csv.GetField(externalIdAttributeName);
                if (string.IsNullOrEmpty(externalId))
                {
                    _logger.Warning("FileConnectorExport.LoadExistingFileContent: Row with empty External ID found, skipping");
                    continue;
                }

                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in headers)
                {
                    var value = csv.GetField(header);
                    if (value != null)
                        row[header] = value;
                }

                rows[externalId] = row;
            }

            _logger.Debug("FileConnectorExport.LoadExistingFileContent: Loaded {Count} existing rows from file", rows.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "FileConnectorExport.LoadExistingFileContent: Error reading existing file, starting fresh");
            rows.Clear();
        }

        return rows;
    }

    /// <summary>
    /// Processes a single pending export, applying changes to the existing rows dictionary.
    /// Returns an ExportResult with the External ID for Create operations.
    /// </summary>
    private ExportResult ProcessPendingExport(
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
                    return ExportResult.Failed($"Create export has no value for External ID attribute '{externalIdAttributeName}'.");
                }

                if (existingRows.ContainsKey(externalId))
                {
                    _logger.Warning("FileConnectorExport.ProcessPendingExport: Create export for '{ExternalId}' but row already exists. Treating as update.",
                        externalId);
                }

                // Build the row from attribute changes
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ApplyAttributeChanges(row, pendingExport, multiValueDelimiter);

                // Include full state from CSO if available (for attributes not in changes)
                ApplyCsoFullState(row, pendingExport, multiValueDelimiter);

                existingRows[externalId] = row;

                return ExportResult.Succeeded(externalId);
            }

            case PendingExportChangeType.Update:
            {
                if (string.IsNullOrEmpty(externalId))
                {
                    _logger.Warning("FileConnectorExport.ProcessPendingExport: Update export has no External ID value");
                    return ExportResult.Failed("Update export has no External ID value.");
                }

                if (!existingRows.TryGetValue(externalId, out var existingRow))
                {
                    _logger.Warning("FileConnectorExport.ProcessPendingExport: Update for '{ExternalId}' but no existing row found. Creating new row.",
                        externalId);
                    existingRow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    existingRows[externalId] = existingRow;
                }

                ApplyAttributeChanges(existingRow, pendingExport, multiValueDelimiter);

                // Include full state from CSO if available (for attributes not in changes)
                ApplyCsoFullState(existingRow, pendingExport, multiValueDelimiter);

                return ExportResult.Succeeded(externalId);
            }

            case PendingExportChangeType.Delete:
            {
                if (string.IsNullOrEmpty(externalId))
                {
                    _logger.Warning("FileConnectorExport.ProcessPendingExport: Delete export has no External ID value");
                    return ExportResult.Failed("Delete export has no External ID value.");
                }

                if (existingRows.Remove(externalId))
                {
                    _logger.Debug("FileConnectorExport.ProcessPendingExport: Removed row for '{ExternalId}'", externalId);
                }
                else
                {
                    _logger.Debug("FileConnectorExport.ProcessPendingExport: Delete for '{ExternalId}' but row not found in file (already removed or never exported)",
                        externalId);
                }

                return ExportResult.Succeeded(externalId);
            }

            default:
                return ExportResult.Failed($"Unsupported change type: {pendingExport.ChangeType}");
        }
    }

    /// <summary>
    /// Gets the External ID value from a pending export.
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
    /// Applies attribute value changes from a pending export to a row dictionary.
    /// </summary>
    private static void ApplyAttributeChanges(
        Dictionary<string, string> row,
        PendingExport pendingExport,
        string multiValueDelimiter)
    {
        foreach (var attrChange in pendingExport.AttributeValueChanges)
        {
            if (attrChange.Attribute?.Name == null)
                continue;

            if (attrChange.ChangeType == PendingExportAttributeChangeType.Remove ||
                attrChange.ChangeType == PendingExportAttributeChangeType.RemoveAll)
            {
                row[attrChange.Attribute.Name] = string.Empty;
            }
            else
            {
                row[attrChange.Attribute.Name] = FormatAttributeValue(attrChange, multiValueDelimiter);
            }
        }
    }

    /// <summary>
    /// Applies full state from the CSO's current attribute values to the row,
    /// for any attributes not already present from the pending export changes.
    /// This ensures the file has complete attribute data for each row.
    /// </summary>
    private void ApplyCsoFullState(
        Dictionary<string, string> row,
        PendingExport pendingExport,
        string multiValueDelimiter)
    {
        if (pendingExport.ConnectedSystemObject?.AttributeValues == null)
            return;

        foreach (var attrValue in pendingExport.ConnectedSystemObject.AttributeValues)
        {
            if (attrValue.Attribute?.Name == null)
                continue;

            // Don't overwrite values that were explicitly set by the pending export changes
            if (row.ContainsKey(attrValue.Attribute.Name))
                continue;

            row[attrValue.Attribute.Name] = FormatCsoAttributeValue(attrValue, multiValueDelimiter);
        }
    }

    /// <summary>
    /// Writes the full-state file from the merged rows dictionary.
    /// </summary>
    private void WriteFullStateFile(
        string filePath,
        string delimiter,
        List<string> attributeColumns,
        Dictionary<string, Dictionary<string, string>> rows)
    {
        _logger.Debug("FileConnectorExport.WriteFullStateFile: Writing {RowCount} rows to {Path}", rows.Count, filePath);

        using var writer = new StreamWriter(filePath);
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter
        });

        // Write header - use attribute columns only (no system columns)
        foreach (var column in attributeColumns)
        {
            csv.WriteField(column);
        }
        csv.NextRecord();

        // Write data rows
        foreach (var row in rows.Values)
        {
            foreach (var column in attributeColumns)
            {
                csv.WriteField(row.TryGetValue(column, out var value) ? value : string.Empty);
            }
            csv.NextRecord();
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

        if (attrChange.DateTimeValue.HasValue)
            return attrChange.DateTimeValue.Value.ToString("O"); // ISO 8601 format

        if (attrChange.GuidValue.HasValue)
            return attrChange.GuidValue.Value.ToString();

        if (attrChange.BoolValue.HasValue)
            return attrChange.BoolValue.Value.ToString().ToLower();

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

        if (attrValue.DateTimeValue.HasValue)
            return attrValue.DateTimeValue.Value.ToString("O");

        if (attrValue.GuidValue.HasValue)
            return attrValue.GuidValue.Value.ToString();

        if (attrValue.BoolValue.HasValue)
            return attrValue.BoolValue.Value.ToString().ToLower();

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
