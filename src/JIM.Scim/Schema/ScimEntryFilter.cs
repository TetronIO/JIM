// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using JIM.Scim.Discovery;
using JIM.Scim.Filtering;

namespace JIM.Scim.Schema;

/// <summary>
/// Evaluates a value filter (RFC 7644 section 3.5.2's <c>valuePath</c>, as in <c>emails[type eq "work"]</c>) against the
/// entries of one multi-valued attribute. Names in the filter are the entry's sub-attributes; an entry of a multi-valued
/// simple attribute is its own <c>value</c>. Each comparison follows the sub-attribute's type: a date compares as an
/// instant, a number as a number, a Boolean as a Boolean (a string <c>"true"</c> included, which some identity providers
/// send), and a string by its <c>caseExact</c> characteristic.
/// </summary>
public static class ScimEntryFilter
{
    private const string ValueSubAttribute = "value";

    /// <summary>
    /// Whether one entry of <paramref name="attribute"/> satisfies the filter.
    /// </summary>
    public static bool Matches(JsonNode? entry, ScimFilter filter, ScimSchemaAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(attribute);

        return filter switch
        {
            ScimLogicalFilter { Operator: ScimLogicalOperator.And } and => Matches(entry, and.Left, attribute) && Matches(entry, and.Right, attribute),
            ScimLogicalFilter or => Matches(entry, or.Left, attribute) || Matches(entry, or.Right, attribute),
            ScimNotFilter not => !Matches(entry, not.Inner, attribute),
            ScimComparisonFilter comparison => Compare(entry, comparison, attribute),
            // A value path nested inside a value path names nothing an entry holds.
            _ => false
        };
    }

    /// <summary>
    /// The sub-attribute values a filter fixes when it is nothing but equalities joined by <c>and</c>, which is enough to
    /// create the entry it selects (<c>emails[type eq "work"]</c> describes <c>{ "type": "work" }</c>). Null otherwise.
    /// </summary>
    public static JsonObject? DescribedEntry(ScimFilter filter, ScimSchemaAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(attribute);

        var entry = new JsonObject();
        return Describe(filter, attribute, entry) ? entry : null;
    }

    private static bool Describe(ScimFilter filter, ScimSchemaAttribute attribute, JsonObject entry)
    {
        switch (filter)
        {
            case ScimLogicalFilter { Operator: ScimLogicalOperator.And } and:
                return Describe(and.Left, attribute, entry) && Describe(and.Right, attribute, entry);

            case ScimComparisonFilter { Operator: ScimComparisonOperator.Eq, Path.SubAttributeName: null, Path.SchemaUrn: null } comparison
                when comparison.Value is { Kind: not ScimFilterValueKind.Null }:
                var sub = ScimResourceSchema.FindSubAttribute(attribute, comparison.Path.AttributeName);
                if (sub == null || entry.ContainsKey(sub.Name!))
                    return false;

                entry[sub.Name!] = comparison.Value.Kind switch
                {
                    ScimFilterValueKind.Boolean => JsonValue.Create(comparison.Value.Text == "true"),
                    ScimFilterValueKind.Number => JsonNode.Parse(comparison.Value.Text!),
                    _ => JsonValue.Create(comparison.Value.Text)
                };
                return true;

            default:
                return false;
        }
    }

    private static bool Compare(JsonNode? entry, ScimComparisonFilter comparison, ScimSchemaAttribute attribute)
    {
        if (comparison.Path.SchemaUrn != null || comparison.Path.SubAttributeName != null)
            return false;

        JsonNode? actual;
        ScimSchemaAttribute? characteristics;
        if (attribute.SubAttributes.Count == 0)
        {
            // A multi-valued simple attribute: each entry is its own value.
            if (!string.Equals(comparison.Path.AttributeName, ValueSubAttribute, StringComparison.OrdinalIgnoreCase))
                return false;
            actual = entry;
            characteristics = attribute;
        }
        else
        {
            characteristics = ScimResourceSchema.FindSubAttribute(attribute, comparison.Path.AttributeName);
            if (characteristics == null || entry is not JsonObject entryObject)
                return false;
            actual = Member(entryObject, characteristics.Name!);
        }

        if (comparison.Operator == ScimComparisonOperator.Pr)
            return IsPresent(actual);

        var expected = comparison.Value;
        if (expected == null)
            return false;

        if (expected.Kind == ScimFilterValueKind.Null)
        {
            var present = IsPresent(actual);
            return comparison.Operator switch
            {
                ScimComparisonOperator.Eq => !present,
                ScimComparisonOperator.Ne => present,
                _ => false
            };
        }

        if (!IsPresent(actual))
            return comparison.Operator == ScimComparisonOperator.Ne;

        return characteristics.Type switch
        {
            ScimAttributeTypes.Boolean => CompareBoolean(actual!, expected, comparison.Operator),
            ScimAttributeTypes.Integer or ScimAttributeTypes.Decimal => CompareNumber(actual!, expected, comparison.Operator),
            ScimAttributeTypes.DateTime => CompareInstant(actual!, expected, comparison.Operator),
            _ => CompareText(actual!, expected, comparison.Operator, characteristics.CaseExact)
        };
    }

    private static bool CompareBoolean(JsonNode actual, ScimFilterValue expected, ScimComparisonOperator op)
    {
        if (!TryBoolean(actual, out var held) || !bool.TryParse(expected.Text, out var wanted))
            return op == ScimComparisonOperator.Ne;

        return op switch
        {
            ScimComparisonOperator.Eq => held == wanted,
            ScimComparisonOperator.Ne => held != wanted,
            _ => false
        };
    }

    private static bool CompareNumber(JsonNode actual, ScimFilterValue expected, ScimComparisonOperator op)
    {
        if (!TryDecimal(actual, out var held) ||
            !decimal.TryParse(expected.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var wanted))
        {
            return op == ScimComparisonOperator.Ne;
        }

        return Ordered(held.CompareTo(wanted), op);
    }

    private static bool CompareInstant(JsonNode actual, ScimFilterValue expected, ScimComparisonOperator op)
    {
        if (!TryInstant(Text(actual), out var held) || !TryInstant(expected.Text, out var wanted))
            return CompareText(actual, expected, op, caseExact: true);

        return Ordered(held.CompareTo(wanted), op);
    }

    private static bool CompareText(JsonNode actual, ScimFilterValue expected, ScimComparisonOperator op, bool caseExact)
    {
        var held = Text(actual);
        var wanted = expected.Text ?? string.Empty;
        if (held == null)
            return op == ScimComparisonOperator.Ne;

        var comparison = caseExact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return op switch
        {
            ScimComparisonOperator.Co => held.Contains(wanted, comparison),
            ScimComparisonOperator.Sw => held.StartsWith(wanted, comparison),
            ScimComparisonOperator.Ew => held.EndsWith(wanted, comparison),
            _ => Ordered(string.Compare(held, wanted, comparison), op)
        };
    }

    private static bool Ordered(int order, ScimComparisonOperator op) => op switch
    {
        ScimComparisonOperator.Eq => order == 0,
        ScimComparisonOperator.Ne => order != 0,
        ScimComparisonOperator.Gt => order > 0,
        ScimComparisonOperator.Ge => order >= 0,
        ScimComparisonOperator.Lt => order < 0,
        ScimComparisonOperator.Le => order <= 0,
        _ => false
    };

    private static bool IsPresent(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject value => value.Count > 0,
        _ => node.GetValueKind() != JsonValueKind.String || node.GetValue<string>().Length > 0
    };

    /// <summary>
    /// A Boolean as JSON, or as the strings some identity providers send for one, in any case.
    /// </summary>
    public static bool TryBoolean(JsonNode node, out bool value)
    {
        ArgumentNullException.ThrowIfNull(node);

        switch (node.GetValueKind())
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.String:
                return bool.TryParse(node.GetValue<string>(), out value);
            default:
                value = false;
                return false;
        }
    }

    private static bool TryDecimal(JsonNode node, out decimal value)
    {
        value = 0;
        return node.GetValueKind() is JsonValueKind.Number or JsonValueKind.String &&
               decimal.TryParse(Text(node), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// An xsd:dateTime instant (RFC 7643 section 2.3.5), with or without an offset (taken as UTC without one).
    /// </summary>
    public static bool TryInstant(string? text, out DateTimeOffset value)
    {
        value = default;
        return text != null &&
               DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }

    private static string? Text(JsonNode node) => node.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => node.ToJsonString(),
        _ => null
    };

    /// <summary>
    /// A member of a JSON object, found without regard to case, as SCIM attribute names are.
    /// </summary>
    public static JsonNode? Member(JsonObject container, string name)
    {
        ArgumentNullException.ThrowIfNull(container);
        var key = Key(container, name);
        return key == null ? null : container[key];
    }

    /// <summary>
    /// The spelling under which a JSON object holds a member, found without regard to case. Null where it holds none.
    /// </summary>
    public static string? Key(JsonObject container, string name)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (container.ContainsKey(name))
            return name;

        return container.Select(member => member.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
    }
}
