// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json;
using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Connectors.Graph;

/// <summary>
/// Reads the attribute values of one Microsoft Graph user or group into JIM's import shape, converting each to its
/// attribute's type. A value that will not convert is reported and the rest of the object is still read, so the object
/// is matched rather than missed. Relationships (members, owners, manager) are read separately, as Graph returns them.
/// </summary>
internal static class GraphObjectReader
{
    public static GraphObjectReadResult Read(JsonElement graphObject, IReadOnlyList<GraphReadAttribute> attributes)
    {
        var result = new GraphObjectReadResult
        {
            Id = graphObject.TryGetProperty(GraphSchemaCatalogue.Id, out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null
        };

        foreach (var attribute in attributes)
        {
            if (!TryGetValue(graphObject, attribute, out var value))
                continue;

            var values = value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(v => !IsEmpty(v)).ToList()
                : [value];
            if (values.Count == 0)
                continue;

            if (attribute.Plurality == AttributePlurality.SingleValued && values.Count > 1)
            {
                result.UnreadableValues.Add(new GraphUnreadableValue(attribute.Name, Describe(value),
                    $"Microsoft Graph returned {values.Count} values for a single-valued attribute."));
                continue;
            }

            var imported = new ConnectedSystemImportObjectAttribute { Name = attribute.Name, Type = attribute.Type };
            var error = values.Select(v => TryAdd(imported, attribute.Type, v)).FirstOrDefault(e => e != null);
            if (error != null)
            {
                result.UnreadableValues.Add(new GraphUnreadableValue(attribute.Name, Describe(value), error));
                continue;
            }

            result.Attributes.Add(imported);
        }

        return result;
    }

    private static bool TryGetValue(JsonElement graphObject, GraphReadAttribute attribute, out JsonElement value)
    {
        value = default;
        if (graphObject.ValueKind != JsonValueKind.Object || !graphObject.TryGetProperty(attribute.Property, out var property))
            return false;

        if (attribute.Member != null)
        {
            if (property.ValueKind != JsonValueKind.Object || !property.TryGetProperty(attribute.Member, out property))
                return false;
        }

        if (IsEmpty(property))
            return false;

        value = property;
        return true;
    }

    /// <summary>Graph sends a cleared value as null, an empty array or an empty string; all mean no value.</summary>
    private static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.String => value.GetString()!.Length == 0,
        _ => false
    };

    /// <returns>Null when the value was added, otherwise why it could not be.</returns>
    private static string? TryAdd(ConnectedSystemImportObjectAttribute attribute, AttributeDataType type, JsonElement value)
    {
        switch (type)
        {
            case AttributeDataType.Text:
                attribute.StringValues.Add(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
                return null;

            case AttributeDataType.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return "Expected true or false.";
                attribute.BoolValues.Add(value.GetBoolean());
                return null;

            case AttributeDataType.Number:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
                    return "Expected a whole number that fits a 32-bit integer.";
                attribute.IntValues.Add(number);
                return null;

            case AttributeDataType.LongNumber:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var longNumber))
                    return "Expected a whole number that fits a 64-bit integer.";
                attribute.LongValues.Add(longNumber);
                return null;

            case AttributeDataType.DateTime:
                if (value.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateTime))
                    return "Expected a date and time in ISO 8601 form.";
                attribute.DateTimeValues.Add(dateTime.UtcDateTime);
                return null;

            case AttributeDataType.Binary:
                if (value.ValueKind != JsonValueKind.String || !value.TryGetBytesFromBase64(out var bytes))
                    return "Expected Base64-encoded binary data.";
                attribute.ByteValues.Add(bytes);
                return null;

            default:
                return $"The {type} type is not read from Microsoft Graph properties.";
        }
    }

    /// <summary>The value as reported back to an administrator, kept short so one odd value cannot flood an Activity.</summary>
    private static string Describe(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        return text.Length > 200 ? text[..200] + "..." : text;
    }
}
