using CsvHelper;
using CsvHelper.Configuration;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;
using System.Dynamic;
using System.Globalization;
namespace JIM.Connectors.File;

/// <summary>
/// Handles CSV export functionality for the FileConnector.
/// </summary>
internal class FileConnectorExport
{
    private readonly IList<ConnectedSystemSettingValue> _settings;
    private readonly IList<PendingExport> _pendingExports;
    private readonly ILogger _logger;

    // Column names for the export file
    private const string ColumnObjectType = "_objectType";
    private const string ColumnExternalId = "_externalId";
    private const string ColumnChangeType = "_changeType";

    internal FileConnectorExport(
        IList<ConnectedSystemSettingValue> settings,
        IList<PendingExport> pendingExports,
        ILogger logger)
    {
        _settings = settings;
        _pendingExports = pendingExports;
        _logger = logger;
    }

    internal void Execute()
    {
        _logger.Debug("FileConnectorExport.Execute: Starting export of {Count} pending exports", _pendingExports.Count);

        if (_pendingExports.Count == 0)
        {
            _logger.Information("FileConnectorExport.Execute: No pending exports to process");
            return;
        }

        var exportPath = GetSettingValue("Export File Path");
        if (string.IsNullOrEmpty(exportPath))
            throw new InvalidOperationException("Export File Path setting is required for export operations.");

        var delimiter = GetSettingValue("Delimiter") ?? ",";
        var multiValueDelimiter = GetSettingValue("Multi-Value Delimiter") ?? "|";
        var timestampedFiles = GetCheckboxValue("Timestamped Files");
        var separateByObjectType = GetCheckboxValue("Separate Files Per Object Type");
        var includeFullState = GetCheckboxValue("Include Full State");

        // Ensure the export directory exists
        if (!Directory.Exists(exportPath))
            Directory.CreateDirectory(exportPath);

        if (separateByObjectType)
        {
            ExportSeparateFiles(exportPath, delimiter, multiValueDelimiter, timestampedFiles, includeFullState);
        }
        else
        {
            ExportSingleFile(exportPath, delimiter, multiValueDelimiter, timestampedFiles, includeFullState);
        }

        _logger.Information("FileConnectorExport.Execute: Completed export of {Count} pending exports", _pendingExports.Count);
    }

    private void ExportSingleFile(string exportPath, string delimiter, string multiValueDelimiter, bool timestampedFiles, bool includeFullState)
    {
        var filename = GenerateFilename("export", timestampedFiles);
        var fullPath = Path.Combine(exportPath, filename);

        _logger.Debug("FileConnectorExport.ExportSingleFile: Writing to {Path}", fullPath);

        using var writer = new StreamWriter(fullPath);
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter
        });

        // Collect all unique attribute names across all pending exports
        var attributeNames = CollectAttributeNames(_pendingExports);

        // Write header
        WriteHeader(csv, attributeNames);

        // Write records
        foreach (var pendingExport in _pendingExports)
        {
            WriteRecord(csv, pendingExport, attributeNames, multiValueDelimiter, includeFullState);
        }

        _logger.Information("FileConnectorExport.ExportSingleFile: Wrote {Count} records to {Path}", _pendingExports.Count, fullPath);
    }

    private void ExportSeparateFiles(string exportPath, string delimiter, string multiValueDelimiter, bool timestampedFiles, bool includeFullState)
    {
        // Group pending exports by object type
        var exportsByObjectType = _pendingExports
            .GroupBy(pe => GetObjectTypeName(pe))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (objectType, exports) in exportsByObjectType)
        {
            var filename = GenerateFilename(objectType, timestampedFiles);
            var fullPath = Path.Combine(exportPath, filename);

            _logger.Debug("FileConnectorExport.ExportSeparateFiles: Writing {Count} {ObjectType} records to {Path}",
                exports.Count, objectType, fullPath);

            using var writer = new StreamWriter(fullPath);
            using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                Delimiter = delimiter
            });

            // Collect attribute names for this object type
            var attributeNames = CollectAttributeNames(exports);

            // Write header
            WriteHeader(csv, attributeNames);

            // Write records
            foreach (var pendingExport in exports)
            {
                WriteRecord(csv, pendingExport, attributeNames, multiValueDelimiter, includeFullState);
            }

            _logger.Information("FileConnectorExport.ExportSeparateFiles: Wrote {Count} {ObjectType} records to {Path}",
                exports.Count, objectType, fullPath);
        }
    }

    private static string GenerateFilename(string baseName, bool timestamped)
    {
        if (timestamped)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            return $"{baseName}_{timestamp}.csv";
        }
        return $"{baseName}.csv";
    }

    private static string GetObjectTypeName(PendingExport pendingExport)
    {
        // For updates/deletes, get from CSO; for creates, we need to derive from attribute metadata
        if (pendingExport.ConnectedSystemObject?.Type != null)
            return pendingExport.ConnectedSystemObject.Type.Name;

        // Fall back to deriving from attribute changes
        var firstAttr = pendingExport.AttributeValueChanges.FirstOrDefault();
        if (firstAttr?.Attribute?.ConnectedSystemObjectType != null)
            return firstAttr.Attribute.ConnectedSystemObjectType.Name;

        return "Unknown";
    }

    private static HashSet<string> CollectAttributeNames(IEnumerable<PendingExport> exports)
    {
        var attributeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var export in exports)
        {
            foreach (var attrChange in export.AttributeValueChanges)
            {
                if (!string.IsNullOrEmpty(attrChange.Attribute?.Name))
                    attributeNames.Add(attrChange.Attribute.Name);
            }
        }

        return attributeNames;
    }

    private static void WriteHeader(CsvWriter csv, HashSet<string> attributeNames)
    {
        // Write system columns first
        csv.WriteField(ColumnObjectType);
        csv.WriteField(ColumnExternalId);
        csv.WriteField(ColumnChangeType);

        // Write attribute columns
        foreach (var attrName in attributeNames.OrderBy(n => n))
        {
            csv.WriteField(attrName);
        }

        csv.NextRecord();
    }

    private static void WriteRecord(CsvWriter csv, PendingExport pendingExport, HashSet<string> attributeNames, string multiValueDelimiter, bool includeFullState)
    {
        // Write system columns
        csv.WriteField(GetObjectTypeName(pendingExport));
        csv.WriteField(GetExternalId(pendingExport));
        csv.WriteField(pendingExport.ChangeType.ToString());

        // Build attribute value lookup
        var attrValues = pendingExport.AttributeValueChanges
            .Where(a => a.Attribute?.Name != null)
            .ToDictionary(
                a => a.Attribute.Name,
                a => FormatAttributeValue(a, multiValueDelimiter),
                StringComparer.OrdinalIgnoreCase);

        // If includeFullState and this is an update/delete, also include current CSO values
        if (includeFullState && pendingExport.ConnectedSystemObject != null)
        {
            foreach (var attrValue in pendingExport.ConnectedSystemObject.AttributeValues)
            {
                if (attrValue.Attribute?.Name != null && !attrValues.ContainsKey(attrValue.Attribute.Name))
                {
                    attrValues[attrValue.Attribute.Name] = FormatCsoAttributeValue(attrValue, multiValueDelimiter);
                }
            }
        }

        // Write attribute values in order
        foreach (var attrName in attributeNames.OrderBy(n => n))
        {
            csv.WriteField(attrValues.TryGetValue(attrName, out var value) ? value : string.Empty);
        }

        csv.NextRecord();
    }

    private static string GetExternalId(PendingExport pendingExport)
    {
        // For updates/deletes, use the CSO's external ID
        if (pendingExport.ConnectedSystemObject?.ExternalIdAttributeValue != null)
            return pendingExport.ConnectedSystemObject.ExternalIdAttributeValue.ToStringNoName() ?? string.Empty;

        // For creates, the external ID might be in the attribute changes (if the target system allows specifying it)
        // Otherwise return empty - the external ID will be assigned by the target system
        return string.Empty;
    }

    private static string FormatAttributeValue(PendingExportAttributeValueChange attrChange, string multiValueDelimiter)
    {
        // Return the appropriate value based on what's populated
        if (!string.IsNullOrEmpty(attrChange.StringValue))
            return attrChange.StringValue;

        if (attrChange.IntValue.HasValue)
            return attrChange.IntValue.Value.ToString();

        if (attrChange.DateTimeValue.HasValue)
            return attrChange.DateTimeValue.Value.ToString("O"); // ISO 8601 format

        if (!string.IsNullOrEmpty(attrChange.UnresolvedReferenceValue))
            return attrChange.UnresolvedReferenceValue;

        return string.Empty;
    }

    private static string FormatCsoAttributeValue(ConnectedSystemObjectAttributeValue attrValue, string multiValueDelimiter)
    {
        // Note: CSO attribute values are stored as individual rows, not as collections
        // For multi-valued attributes, multiple ConnectedSystemObjectAttributeValue records exist
        // This method formats a single value

        if (!string.IsNullOrEmpty(attrValue.StringValue))
            return attrValue.StringValue;

        if (attrValue.IntValue.HasValue)
            return attrValue.IntValue.Value.ToString();

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

    private bool GetCheckboxValue(string settingName)
    {
        return _settings.SingleOrDefault(s => s.Setting.Name == settingName)?.CheckboxValue ?? false;
    }
}
