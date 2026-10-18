// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Models.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Filtering;
using JIM.Scim.Messages;
using JIM.Scim.Schema;

namespace JIM.Scim.Service;

/// <summary>
/// Turns a list request's filter (RFC 7644 section 3.4.2.2) into the store's predicate model, from the resource type's
/// definitions alone:
/// <list type="bullet">
/// <item><c>eq</c> on an attribute that must be unique reads the unique-value rows, with the value normalised as writes
/// normalise it, so <c>userName eq</c> is an index lookup however large the store;</item>
/// <item><c>eq</c>, <c>co</c> and <c>sw</c> on any other string attribute, and <c>eq</c> on a Boolean, are matched in the
/// payload, with the attribute's <c>caseExact</c>;</item>
/// <item><c>id eq</c>, and <c>eq</c>, <c>gt</c>, <c>ge</c>, <c>lt</c> and <c>le</c> on <c>meta.lastModified</c>, compare
/// the store's own columns;</item>
/// <item><c>and</c> and <c>or</c> join them.</item>
/// </list>
/// Anything else is refused with <c>400</c> <c>invalidFilter</c>: other operators, <c>not</c>, value paths, reference,
/// read-only and write-only attributes, and attribute types the subset does not compare. A refusal never approximates.
/// </summary>
public static class ScimStoreFilterTranslator
{
    private const string IdAttribute = "id";
    private const string MetaAttribute = "meta";
    private const string LastModifiedSubAttribute = "lastModified";

    /// <summary>
    /// Parses and translates a filter.
    /// </summary>
    public static ScimFilterTranslation Translate(string filter, ScimServiceResourceType type)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(type);

        ScimFilter parsed;
        try
        {
            parsed = ScimFilterParser.Parse(filter);
        }
        catch (ScimFilterException)
        {
            // The parser's message points into the filter, which may carry a literal, so none of it is repeated.
            return Refuse("The filter is not a valid SCIM filter.");
        }

        try
        {
            return new ScimFilterTranslation { Filter = Translate(parsed, type) };
        }
        catch (Refusal refusal)
        {
            return Refuse(refusal.Message);
        }
    }

    private static ScimStoreFilter Translate(ScimFilter filter, ScimServiceResourceType type) => filter switch
    {
        ScimLogicalFilter logical => new ScimStoreLogicalFilter(
            logical.Operator == ScimLogicalOperator.And ? ScimStoreLogicalOperator.And : ScimStoreLogicalOperator.Or,
            Translate(logical.Left, type),
            Translate(logical.Right, type)),
        ScimComparisonFilter comparison => Comparison(comparison, type),
        ScimNotFilter => throw new Refusal("The not operator is not supported."),
        ScimValuePathFilter => throw new Refusal("Value path filters are not supported."),
        _ => throw new Refusal("The filter is not supported.")
    };

    private static ScimStoreFilter Comparison(ScimComparisonFilter comparison, ScimServiceResourceType type)
    {
        var path = comparison.Path;
        var schemaUrn = path.SchemaUrn;
        if (schemaUrn != null && !type.Schema.Declares(schemaUrn))
            throw new Refusal("The filter names a schema this resource type does not declare.");
        var extensionUrn = schemaUrn != null && type.Schema.IsExtension(schemaUrn) ? type.Schema.CanonicalUrn(schemaUrn) : null;
        var label = path.SubAttributeName == null ? path.AttributeName : $"{path.AttributeName}.{path.SubAttributeName}";

        if (extensionUrn == null && string.Equals(path.AttributeName, IdAttribute, StringComparison.OrdinalIgnoreCase) && path.SubAttributeName == null)
            return Id(comparison);
        if (extensionUrn == null && string.Equals(path.AttributeName, MetaAttribute, StringComparison.OrdinalIgnoreCase))
            return LastModified(comparison);

        var attribute = type.Schema.Find(extensionUrn, path.AttributeName)
                        ?? throw new Refusal($"The attribute '{label}' is not defined for this resource type.");
        if (ScimResourceSchema.IsWriteOnly(attribute))
            throw new Refusal($"The attribute '{label}' cannot be filtered on.");
        if (ScimResourceSchema.IsReadOnly(attribute))
            throw new Refusal($"The attribute '{label}' is stated by the service provider and cannot be filtered on.");
        if (type.FindReferenceAttribute(extensionUrn == null ? attribute.Name! : $"{extensionUrn}:{attribute.Name}") != null)
            throw new Refusal($"The attribute '{label}' refers to other resources and cannot be filtered on.");

        var compared = attribute;
        string? entryMember = null;
        List<string> jsonPath = extensionUrn == null ? [attribute.Name!] : [extensionUrn, attribute.Name!];
        if (path.SubAttributeName != null)
        {
            var sub = ScimResourceSchema.FindSubAttribute(attribute, path.SubAttributeName)
                      ?? throw new Refusal($"The attribute '{label}' is not defined for this resource type.");
            if (ScimResourceSchema.IsWriteOnly(sub) || ScimResourceSchema.IsReadOnly(sub))
                throw new Refusal($"The attribute '{label}' cannot be filtered on.");
            compared = sub;
            if (attribute.MultiValued)
                entryMember = sub.Name;
            else
                jsonPath.Add(sub.Name!);
        }
        else if (IsComplex(attribute))
        {
            throw new Refusal($"The attribute '{label}' is complex; filter on one of its sub-attributes.");
        }

        if (string.Equals(compared.Type, ScimAttributeTypes.Boolean, StringComparison.OrdinalIgnoreCase))
            return Boolean(comparison, jsonPath, attribute.MultiValued, label);

        if (!IsText(compared.Type))
            throw new Refusal($"Filtering on the attribute '{label}' is not supported.");

        var text = TextValue(comparison, label);
        var match = comparison.Operator switch
        {
            ScimComparisonOperator.Eq => ScimStoreTextMatch.Equal,
            ScimComparisonOperator.Co => ScimStoreTextMatch.Contains,
            ScimComparisonOperator.Sw => ScimStoreTextMatch.StartsWith,
            _ => throw UnsupportedOperator(comparison.Operator, label)
        };

        // Equality on an attribute that must be unique is an index lookup on the unique-value rows.
        if (match == ScimStoreTextMatch.Equal && path.SubAttributeName == null)
        {
            var name = extensionUrn == null ? attribute.Name! : $"{extensionUrn}:{attribute.Name}";
            var unique = type.UniqueAttributes.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
            if (unique != null)
                return new ScimStoreUniqueValueFilter(unique.Name, unique.Normalise(JsonValue.Create(text))!);
        }

        return new ScimStoreTextFilter(jsonPath, attribute.MultiValued, entryMember, match, text, compared.CaseExact);
    }

    private static ScimStoreFilter Id(ScimComparisonFilter comparison)
    {
        if (comparison.Operator != ScimComparisonOperator.Eq)
            throw UnsupportedOperator(comparison.Operator, IdAttribute);

        var text = TextValue(comparison, IdAttribute);
        return Guid.TryParse(text, out var id) ? new ScimStoreIdFilter(id) : ScimStoreNothingFilter.Instance;
    }

    private static ScimStoreFilter LastModified(ScimComparisonFilter comparison)
    {
        const string label = "meta.lastModified";
        if (!string.Equals(comparison.Path.SubAttributeName, LastModifiedSubAttribute, StringComparison.OrdinalIgnoreCase))
            throw new Refusal("Of the meta attributes, only meta.lastModified can be filtered on.");

        var stated = comparison.Operator switch
        {
            ScimComparisonOperator.Eq => ScimStoreComparison.Equal,
            ScimComparisonOperator.Gt => ScimStoreComparison.GreaterThan,
            ScimComparisonOperator.Ge => ScimStoreComparison.GreaterOrEqual,
            ScimComparisonOperator.Lt => ScimStoreComparison.LessThan,
            ScimComparisonOperator.Le => ScimStoreComparison.LessOrEqual,
            _ => throw UnsupportedOperator(comparison.Operator, label)
        };

        if (!ScimEntryFilter.TryInstant(TextValue(comparison, label), out var instant))
            throw new Refusal($"The value compared to '{label}' is not a date and time.");

        return new ScimStoreLastModifiedFilter(stated, instant.UtcDateTime);
    }

    private static ScimStoreBooleanFilter Boolean(ScimComparisonFilter comparison, List<string> jsonPath, bool multiValued, string label)
    {
        if (comparison.Operator != ScimComparisonOperator.Eq || multiValued)
            throw UnsupportedOperator(comparison.Operator, label);
        if (comparison.Value is not { Kind: ScimFilterValueKind.Boolean, Text: { } text })
            throw new Refusal($"The attribute '{label}' is compared to a Boolean.");

        return new ScimStoreBooleanFilter(jsonPath, text == "true");
    }

    private static string TextValue(ScimComparisonFilter comparison, string label) =>
        comparison.Value is { Kind: ScimFilterValueKind.String, Text: { } text }
            ? text
            : throw new Refusal($"The attribute '{label}' is compared to a string.");

    private static bool IsText(string? type) =>
        string.Equals(type, ScimAttributeTypes.String, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, ScimAttributeTypes.Reference, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, ScimAttributeTypes.Binary, StringComparison.OrdinalIgnoreCase);

    private static bool IsComplex(ScimSchemaAttribute attribute) =>
        string.Equals(attribute.Type, ScimAttributeTypes.Complex, StringComparison.OrdinalIgnoreCase) || attribute.SubAttributes.Count > 0;

    private static Refusal UnsupportedOperator(ScimComparisonOperator op, string label) =>
        new($"The operator '{op.ToString().ToLowerInvariant()}' is not supported for '{label}'.");

    private static ScimFilterTranslation Refuse(string detail) =>
        new() { Error = ScimError.ForStatus(400, detail, ScimErrorTypes.InvalidFilter) };

    /// <summary>
    /// A filter outside the subset, unwinding the translation; its message is the error's detail.
    /// </summary>
    private sealed class Refusal(string message) : Exception(message);
}
