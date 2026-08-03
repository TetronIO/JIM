// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Utilities;

namespace JIM.Scim.Schema;

/// <summary>
/// Reads a SCIM resource into JIM attribute values, following the same flattening convention
/// <see cref="ScimAttributeMapper"/> used to build the schema.
/// <para>
/// The two must stay in step: an attribute the mapper published but the reader cannot find is an
/// Attribute Flow target that silently never receives a value. Each flattened attribute therefore
/// carries a structural accessor, so reading is a lookup rather than a re-parse of the SCIM path.
/// </para>
/// </summary>
public static class ScimResourceReader
{
    private const string ValueSubAttribute = "value";
    private const string RefSubAttribute = "$ref";
    private const string TypeSubAttribute = "type";
    private const string PrimarySubAttribute = "primary";

    /// <summary>
    /// Reads one resource.
    /// </summary>
    /// <param name="resource">The resource as returned by the service provider.</param>
    /// <param name="attributes">The flattened schema attributes to look for.</param>
    public static ScimResourceReadResult Read(JsonElement resource, IReadOnlyList<ScimFlattenedAttribute> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        if (resource.ValueKind != JsonValueKind.Object)
        {
            return new ScimResourceReadResult
            {
                Error = "The service provider returned a resource that is not a JSON object."
            };
        }

        var read = new List<ConnectedSystemImportObjectAttribute>();
        var warnings = new List<string>();

        foreach (var attribute in attributes)
        {
            var values = ReadValues(resource, attribute, warnings);
            if (values.Count == 0)
                continue;

            var converted = Convert(attribute, values, out var error);
            if (error != null)
            {
                // Any value JIM cannot hold faithfully fails the whole object. Importing the rest would
                // present a partial object as a complete one, which synchronisation would then act on.
                return new ScimResourceReadResult { Error = error };
            }

            if (converted != null)
                read.Add(converted);
        }

        return new ScimResourceReadResult { Attributes = read, Warnings = warnings };
    }

    /// <summary>
    /// Reads a resource's <c>meta.lastModified</c>, which delta import watermarks against.
    /// <para>
    /// Taken from the resource itself rather than from the attributes just imported, because the
    /// watermark must not depend on an administrator having selected <c>meta.lastModified</c> for
    /// import: a delta strategy that silently stopped working when an attribute was deselected would be
    /// worse than one that never worked at all.
    /// </para>
    /// </summary>
    /// <returns>True where the resource carries a readable last-modified date.</returns>
    public static bool TryReadLastModified(JsonElement resource, out DateTimeOffset lastModified)
    {
        lastModified = default;

        if (!TryGetProperty(resource, "meta", out var meta) || !TryGetProperty(meta, "lastModified", out var value))
            return false;

        if (value.ValueKind != JsonValueKind.String)
            return false;

        return DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out lastModified);
    }

    /// <summary>
    /// Locates the raw JSON values for one flattened attribute, following its accessor.
    /// </summary>
    private static List<JsonElement> ReadValues(JsonElement resource, ScimFlattenedAttribute attribute, List<string> warnings)
    {
        // Extension attributes are nested under a JSON member named by their schema URN.
        var container = resource;
        if (attribute.ExtensionUrn != null && !TryGetProperty(resource, attribute.ExtensionUrn, out container))
            return [];

        if (!TryGetProperty(container, attribute.SourceAttributeName, out var source) || source.ValueKind == JsonValueKind.Null)
            return [];

        return attribute.Access switch
        {
            ScimValueAccess.Simple => Scalars(source),
            ScimValueAccess.ComplexSubAttribute => ReadSubAttribute(source, attribute.SubAttributeName),
            ScimValueAccess.CanonicalSlot => ReadCanonicalSlot(source, attribute, warnings),
            ScimValueAccess.ComplexReference => ReadReferences(source),
            _ => []
        };
    }

    /// <summary>
    /// Reads a named sub-attribute out of a complex value, or out of each entry where the attribute is
    /// multi-valued.
    /// </summary>
    private static List<JsonElement> ReadSubAttribute(JsonElement source, string? subAttributeName)
    {
        if (subAttributeName == null)
            return [];

        return Entries(source)
            .Select(entry => TryGetProperty(entry, subAttributeName, out var value) ? value : default)
            .Where(value => value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            .ToList();
    }

    /// <summary>
    /// Selects the entry of a multi-valued complex attribute by canonical type or primary flag, then
    /// reads the slot's sub-attribute from it.
    /// </summary>
    private static List<JsonElement> ReadCanonicalSlot(JsonElement source, ScimFlattenedAttribute attribute, List<string> warnings)
    {
        var matches = Entries(source).Where(entry => Matches(entry, attribute)).ToList();
        if (matches.Count == 0)
            return [];

        // The slot is single-valued by design, so a second matching entry is data the slot cannot hold.
        // Reporting it beats importing one silently and presenting a partial value as a complete one.
        if (matches.Count > 1)
        {
            var selector = attribute.SelectsPrimary ? "marked primary" : $"of type '{attribute.CanonicalType}'";
            warnings.Add($"The service provider returned {matches.Count} '{attribute.SourceAttributeName}' entries {selector}, " +
                         $"but '{attribute.Name}' holds one value. The first was imported and the rest were not.");
        }

        return ReadSubAttribute(matches[0], attribute.SubAttributeName);
    }

    private static bool Matches(JsonElement entry, ScimFlattenedAttribute attribute)
    {
        if (entry.ValueKind != JsonValueKind.Object)
            return false;

        if (attribute.SelectsPrimary)
            return TryGetProperty(entry, PrimarySubAttribute, out var primary) && primary.ValueKind == JsonValueKind.True;

        // RFC 7643 section 2.1: attribute values of canonical type are compared without regard to case.
        return TryGetProperty(entry, TypeSubAttribute, out var type)
               && type.ValueKind == JsonValueKind.String
               && string.Equals(type.GetString(), attribute.CanonicalType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the referenced identifiers from a complex attribute carrying <c>$ref</c>, preferring the
    /// <c>value</c> sub-attribute (the referenced resource's id) and falling back to the URI.
    /// </summary>
    private static List<JsonElement> ReadReferences(JsonElement source)
    {
        return Entries(source)
            .Select(entry =>
            {
                if (TryGetProperty(entry, ValueSubAttribute, out var value) && value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                    return value;
                return TryGetProperty(entry, RefSubAttribute, out var reference) ? reference : default;
            })
            .Where(value => value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            .ToList();
    }

    /// <summary>
    /// Treats a value uniformly whether the provider sent one entry or an array of them. Providers do
    /// send a bare object where the schema says multi-valued, and the reverse.
    /// </summary>
    private static List<JsonElement> Entries(JsonElement source)
    {
        return source.ValueKind == JsonValueKind.Array ? source.EnumerateArray().ToList() : [source];
    }

    private static List<JsonElement> Scalars(JsonElement source)
    {
        return Entries(source).Where(v => v.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)).ToList();
    }

    /// <summary>
    /// Converts located JSON values into the typed list the import object expects, returning null when
    /// nothing usable survived.
    /// </summary>
    private static ConnectedSystemImportObjectAttribute? Convert(ScimFlattenedAttribute attribute, List<JsonElement> values, out string? error)
    {
        error = null;
        var converted = new ConnectedSystemImportObjectAttribute { Name = attribute.Name, Type = attribute.Type };

        foreach (var value in values)
        {
            switch (attribute.Type)
            {
                case AttributeDataType.Boolean:
                    // Booleans are single-valued by nature: several values cannot be told apart.
                    converted.BoolValue = value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : converted.BoolValue,
                        _ => converted.BoolValue
                    };
                    break;

                case AttributeDataType.LongNumber:
                    if (TryGetInt64(value, out var longValue))
                        converted.LongValues.Add(longValue);
                    break;

                case AttributeDataType.Number:
                    if (TryGetInt64(value, out var intCandidate) && intCandidate is >= int.MinValue and <= int.MaxValue)
                        converted.IntValues.Add((int)intCandidate);
                    break;

                case AttributeDataType.Decimal:
                    // Decimals are parsed only through DecimalAttributeValue, never via double, so no
                    // precision is lost on the way in.
                    var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                    if (DecimalAttributeValue.TryParse(raw, out var decimalValue))
                    {
                        converted.DecimalValues.Add(decimalValue);
                        break;
                    }

                    // Out of range or unparseable: rounding it would corrupt the value silently, and
                    // synchronisation integrity outranks importing the object at all.
                    error = $"The service provider returned a value for '{attribute.Name}' that is not a decimal JIM can hold: '{LogSanitiser.Sanitise(raw)}'.";
                    return null;

                case AttributeDataType.DateTime:
                    if (TryGetDateTime(value, out var dateTimeValue))
                        converted.DateTimeValue = dateTimeValue;
                    break;

                case AttributeDataType.Binary:
                    if (TryGetBinary(value, out var bytes))
                        converted.ByteValues.Add(bytes);
                    break;

                case AttributeDataType.Guid:
                    if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var guidValue))
                        converted.GuidValues.Add(guidValue);
                    break;

                case AttributeDataType.Reference:
                    var referenceValue = AsString(value);
                    if (!string.IsNullOrWhiteSpace(referenceValue))
                        converted.ReferenceValues.Add(referenceValue);
                    break;

                default:
                    var stringValue = AsString(value);
                    if (!string.IsNullOrEmpty(stringValue))
                        converted.StringValues.Add(stringValue);
                    break;
            }
        }

        return HasValue(converted) ? converted : null;
    }

    private static bool HasValue(ConnectedSystemImportObjectAttribute attribute)
    {
        return attribute.StringValues.Count > 0
               || attribute.ReferenceValues.Count > 0
               || attribute.IntValues.Count > 0
               || attribute.LongValues.Count > 0
               || attribute.DecimalValues.Count > 0
               || attribute.GuidValues.Count > 0
               || attribute.ByteValues.Count > 0
               || attribute.DateTimeValue.HasValue
               || attribute.BoolValue.HasValue;
    }

    /// <summary>
    /// Reads a value as a string whatever JSON type the provider used. Providers commonly send numbers
    /// and booleans as strings, and a schema-typed string attribute holding a number is not an error.
    /// </summary>
    private static string? AsString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null
        };
    }

    private static bool TryGetInt64(JsonElement value, out long result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result))
            return true;

        if (value.ValueKind == JsonValueKind.String)
            return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

        result = 0;
        return false;
    }

    /// <summary>
    /// Reads an ISO 8601 timestamp, normalised to UTC. JIM stores every date and time in UTC, and a
    /// provider is free to send an offset.
    /// </summary>
    private static bool TryGetDateTime(JsonElement value, out DateTime result)
    {
        result = default;
        if (value.ValueKind != JsonValueKind.String)
            return false;

        if (!DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return false;

        result = parsed.UtcDateTime;
        return true;
    }

    private static bool TryGetBinary(JsonElement value, out byte[] result)
    {
        result = [];
        if (value.ValueKind != JsonValueKind.String)
            return false;

        var encoded = value.GetString();
        if (string.IsNullOrEmpty(encoded))
            return false;

        var buffer = new byte[((encoded.Length * 3) + 3) / 4];
        if (!System.Convert.TryFromBase64String(encoded, buffer, out var written))
            return false;

        result = buffer[..written];
        return true;
    }

    /// <summary>
    /// Property lookup that honours RFC 7643 section 2.1: SCIM attribute names are case insensitive.
    /// </summary>
    private static bool TryGetProperty(JsonElement source, string name, out JsonElement value)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        if (source.TryGetProperty(name, out value))
            return true;

        foreach (var property in source.EnumerateObject().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }
}
