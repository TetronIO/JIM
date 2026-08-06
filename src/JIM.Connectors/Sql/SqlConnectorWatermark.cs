// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JIM.Connectors.Sql;

/// <summary>
/// How a Delta Import finds out what has changed. An enumeration rather than a pair of flags, because
/// the mechanisms are alternatives: an estate uses one of them, and a third (provider-native change
/// detection) joins the list without any of this configuration changing shape.
/// </summary>
internal enum SqlDeltaImportMode
{
    /// <summary>
    /// No mode has been chosen, so Delta Imports cannot run.
    /// </summary>
    NotSet = 0,

    /// <summary>
    /// Changes read from a customer-maintained change-log table or view, carrying the anchor, a change
    /// type and a monotonic sequence. The only mode that observes a deletion.
    /// </summary>
    ChangeLogTable = 1,

    /// <summary>
    /// Changes detected from a last-modified or version column on the object type's own source. Creates
    /// and updates only.
    /// </summary>
    WatermarkColumn = 2
}

/// <summary>
/// A value read out of a database and carried between runs as text: a watermark, or a delta page's
/// position. The type travels with it because the column it came from is not part of the Connected
/// System's schema (a change log is not an object type), so there is nothing else to say how the string
/// is to be read back and bound.
/// </summary>
internal sealed record SqlDeltaValue(string Value, AttributeDataType Type);

/// <summary>
/// Where each Object Type's Delta Import had got to when the last run began, persisted between runs in
/// <see cref="JIM.Models.Staging.ConnectedSystemImportResult.PersistedConnectorData"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mode is recorded with the values, and is what makes them meaningful.</b> A change log's
/// sequence number says nothing about a last-modified column, so a watermark written in one mode must
/// never be compared against the other; a Connected System whose mode has changed is re-baselined
/// rather than trusted.
/// </para>
/// <para>
/// <b>The watermark is a marker, not a point in time.</b> Where it holds a date, that date is compared
/// against the column's own values and nothing else, so it is never interpreted in the Connected
/// System's declared time zone the way an imported date and time is. Interpreting it would move the
/// boundary by the offset and re-read, or skip, whatever fell inside it.
/// </para>
/// <para>
/// <b>Every source keeps its own watermark, never a single maximum across them.</b> In Watermark Column
/// mode an object type's related tables are separate sources with separate columns: a version number
/// here, a last-modified timestamp there, each moved by its own writers. One value taken across all of
/// them would be the highest any of them had reached, and using it as the others' boundary would skip
/// everything that had happened to them below it, permanently. Per-source watermarks cost a few bytes of
/// persisted state and are the only arrangement that cannot lose a change.
/// </para>
/// </remarks>
internal sealed record SqlConnectorWatermark
{
    private static readonly JsonSerializerOptions SerialisationOptions = new()
    {
        PropertyNameCaseInsensitive = true,

        // The persisted value is written to the diagnostic log by the Worker and read by whoever is
        // investigating a Delta Import, so the mode and the types read as their own names rather than
        // as numbers whose meaning lives in a source file.
        Converters = { new JsonStringEnumConverter() }
    };

    // Public members of an internal type: nothing is exposed outside this assembly, and the serialiser
    // only writes what it can see.
    public required SqlDeltaImportMode Mode { get; init; }

    /// <summary>
    /// The watermark per configured Object Type, keyed by its name. An Object Type absent from here has
    /// no watermark yet (it was added to the document after the last run), and reads its changes from
    /// the beginning, which is the only answer that cannot miss one.
    /// </summary>
    public Dictionary<string, SqlDeltaValue> ObjectTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The watermark per related table of each configured Object Type, keyed by the Object Type's name
    /// and then by the attribute the related table supplies. Only Watermark Column mode records these:
    /// a change log states what happened to the object however it happened, so its related tables have
    /// nothing of their own to remember.
    /// </summary>
    /// <remarks>
    /// A related table absent from here has no watermark yet, which is what a Connected System imported
    /// before this Connector watched related tables looks like, and what a newly added related table
    /// looks like. It reads from the beginning once, which is the only answer that cannot miss a change.
    /// </remarks>
    public Dictionary<string, Dictionary<string, SqlDeltaValue>> RelatedTables { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What JIM holds for one Object Type's related tables, empty where it holds nothing for any of them.
    /// </summary>
    internal IReadOnlyDictionary<string, SqlDeltaValue> RelatedTablesFor(string objectTypeName) =>
        RelatedTables.TryGetValue(objectTypeName, out var relatedTables) ? relatedTables : new Dictionary<string, SqlDeltaValue>(StringComparer.OrdinalIgnoreCase);

    internal string Serialise() => JsonSerializer.Serialize(this, SerialisationOptions);

    /// <summary>
    /// Reads a watermark JIM persisted from an earlier run.
    /// </summary>
    /// <returns>
    /// The watermark, or null where there is none to read or it cannot be read. Null is an answer rather
    /// than a failure: it is what a Connected System that has never been imported looks like, and the
    /// caller decides what to do about it.
    /// </returns>
    internal static SqlConnectorWatermark? TryRead(string? persistedConnectorData)
    {
        if (string.IsNullOrWhiteSpace(persistedConnectorData))
            return null;

        SqlConnectorWatermark? watermark;
        try
        {
            watermark = JsonSerializer.Deserialize<SqlConnectorWatermark>(persistedConnectorData, SerialisationOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (watermark == null || watermark.Mode == SqlDeltaImportMode.NotSet)
            return null;

        // Object type and attribute names are matched the way they are everywhere else in this Connector,
        // which the deserialiser's own ordinal dictionaries would not do.
        return watermark with
        {
            ObjectTypes = new Dictionary<string, SqlDeltaValue>(watermark.ObjectTypes, StringComparer.OrdinalIgnoreCase),
            RelatedTables = watermark.RelatedTables.ToDictionary(
                objectType => objectType.Key,
                objectType => new Dictionary<string, SqlDeltaValue>(objectType.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// Describes a value a database handed back, so it can be carried as text and bound back later.
    /// </summary>
    /// <returns>The described value, or null where the column had none (an empty change log has no highest sequence).</returns>
    /// <exception cref="NotSupportedException">The value's type cannot order a change set, so it could never be a watermark.</exception>
    internal static SqlDeltaValue? Describe(object? value)
    {
        if (value == null || value == DBNull.Value)
            return null;

        var type = value switch
        {
            // A value carrying its own offset is normalised to UTC before it is rendered, exactly as an
            // anchor is, so the same instant always produces the same text.
            DateTimeOffset or DateTime => AttributeDataType.DateTime,
            byte[] => AttributeDataType.Binary,
            Guid => AttributeDataType.Guid,
            string => AttributeDataType.Text,
            decimal or float or double => AttributeDataType.Decimal,
            long or ulong => AttributeDataType.LongNumber,
            int or uint or short or ushort or byte or sbyte => AttributeDataType.Number,
            _ => throw new NotSupportedException($"A {value.GetType().Name} value cannot order a change set, so it cannot be used as a Delta Import watermark.")
        };

        var normalised = value is DateTimeOffset dateTimeOffset ? dateTimeOffset.UtcDateTime : value;
        return new SqlDeltaValue(SqlAnchorValue.ToTokenString(normalised, type), type);
    }
}
