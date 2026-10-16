// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Models.Activities;

/// <summary>
/// Every value an import found on one object but could not read (#2066), as recorded on the object's
/// <see cref="ActivityRunProfileExecutionItem.AttributeValueErrorsJson"/>, so the item page, the REST API and PowerShell
/// can show what came in and why it was refused.
/// </summary>
/// <remarks>
/// Bounded so one malformed source cannot bloat the Activity: at most <see cref="MaxRecordedValues"/> values are kept,
/// each shortened to <see cref="MaxValueLength"/> characters, and <see cref="Total"/> says how many there were.
/// </remarks>
public class ImportedAttributeValueErrors
{
    /// <summary>The most values recorded for one object; <see cref="Total"/> counts the rest.</summary>
    public const int MaxRecordedValues = 1000;

    /// <summary>The longest a recorded value is kept at; a longer one is cut short and marked so.</summary>
    public const int MaxValueLength = 1000;

    // Follows the established stored-JSON conventions (ConfigurationSnapshotService, ActivityErrorDetail,
    // MvoDeletionPolicySnapshot): camelCase properties, string enum values, nulls omitted.
    private static readonly JsonSerializerOptions SerialiserOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    /// <summary>
    /// How many values the Connector could not read, including any beyond those recorded in <see cref="Values"/>.
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// The values, in the order the Connector reported them.
    /// </summary>
    public List<ImportedAttributeValueError> Values { get; set; } = [];

    /// <summary>
    /// Records what a Connector reported, with each attribute's type from the Object Type's schema; null when there was
    /// nothing to record.
    /// </summary>
    /// <param name="reported">The values the Connector could not read.</param>
    /// <param name="objectType">The Object Type the object was imported as, for each attribute's type; null where unknown.</param>
    public static ImportedAttributeValueErrors? Record(
        IReadOnlyList<ConnectedSystemImportObjectAttributeValueError>? reported,
        ConnectedSystemObjectType? objectType)
    {
        if (reported is not { Count: > 0 })
            return null;

        return new ImportedAttributeValueErrors
        {
            Total = reported.Count,
            Values = reported
                .Take(MaxRecordedValues)
                .Select(value => new ImportedAttributeValueError
                {
                    AttributeName = value.AttributeName,
                    AttributeType = objectType?.Attributes
                        .FirstOrDefault(a => a.Name.Equals(value.AttributeName, StringComparison.OrdinalIgnoreCase))?.Type,
                    Value = Shorten(value.Value),
                    Reason = value.Reason,
                    Source = value.Source
                })
                .ToList()
        };
    }

    /// <summary>
    /// Serialises the record to the JSON document stored on the execution item.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerialiserOptions);

    /// <summary>
    /// Reads back a record written by <see cref="ToJson"/>. Returns null when the value is absent or no longer parses,
    /// so a rendering caller falls back to the item's message rather than failing.
    /// </summary>
    public static ImportedAttributeValueErrors? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ImportedAttributeValueErrors>(json, SerialiserOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Shorten(string? value) =>
        value is { Length: > MaxValueLength } ? $"{value[..MaxValueLength]}..." : value;
}
