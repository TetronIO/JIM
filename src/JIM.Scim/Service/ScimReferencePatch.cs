// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using JIM.Scim.Filtering;
using JIM.Scim.Messages;
using JIM.Scim.Schema;

namespace JIM.Scim.Service;

/// <summary>
/// Recognises a PATCH that only adds, removes, clears or replaces values of multi-valued reference attributes (a Group's
/// <c>members</c>), which the server applies as reference row changes without loading what the resource already holds:
/// one member added to a Group of 100,000 reads none of them.
/// <para>
/// It does not re-implement PATCH. Every value such a request can touch is one it names, by an entry's <c>value</c> or a
/// <c>value eq</c> filter, so the request's effect on everything else is nothing, or, for a clear or a replace, removal.
/// <see cref="ScimReferencePatchPlan.Resolve"/> therefore applies the request with the real applier to a resource
/// holding only the named references the resource holds, and reads the changes off the result.
/// </para>
/// </summary>
public static class ScimReferencePatch
{
    private const string ValueMember = "value";
    private static readonly string[] Operations = [ScimPatchOperations.Add, ScimPatchOperations.Replace, ScimPatchOperations.Remove];

    /// <summary>
    /// A plan for a request in the direct path's reach, or null for any other request, which the server applies to the
    /// whole resource instead. Nothing about a null is an error: the applier decides whether the request is valid.
    /// </summary>
    public static ScimReferencePatchPlan? TryPlan(IReadOnlyList<ScimPatchOperation> operations, ScimServiceResourceType type)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(type);

        if (operations.Count == 0)
            return null;

        var mentioned = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var operation in operations)
        {
            var op = Operations.FirstOrDefault(known => string.Equals(known, operation.Op, StringComparison.OrdinalIgnoreCase));
            if (op == null || string.IsNullOrWhiteSpace(operation.Path))
                return null;

            ScimPatchPath path;
            try
            {
                path = ScimFilterParser.ParsePath(operation.Path);
            }
            catch (ScimFilterException)
            {
                return null;
            }

            if (path.Attribute.SubAttributeName != null || path.SubAttributeName != null)
                return null;

            var attribute = Resolve(type, path.Attribute);
            if (attribute is not { MultiValued: true } || IsRequired(type, attribute))
                return null;

            var ids = mentioned.TryGetValue(attribute.Name, out var existing) ? existing : mentioned[attribute.Name] = [];

            if (path.ValueFilter != null)
            {
                // Only a remove selecting by value: anything else can reach references the request does not name.
                if (op != ScimPatchOperations.Remove || !TryValueEquality(path.ValueFilter, out var literal))
                    return null;
                if (Guid.TryParse(literal, out var id))
                    ids.Add(id);
                continue;
            }

            if (operation.Value == null && op != ScimPatchOperations.Add)
            {
                replaced.Add(attribute.Name);
                continue;
            }

            if (op == ScimPatchOperations.Replace)
                replaced.Add(attribute.Name);

            IEnumerable<JsonNode?> entries = operation.Value is JsonArray array ? array : new[] { operation.Value };
            foreach (var entry in entries)
            {
                // An entry with no value could match any held reference (a remove listing { "type": "User" }).
                if (entry is not JsonObject members || ScimEntryFilter.Member(members, ValueMember) is not JsonValue value ||
                    value.GetValueKind() != JsonValueKind.String)
                    return null;
                if (Guid.TryParse(value.GetValue<string>(), out var id))
                    ids.Add(id);
            }
        }

        return new ScimReferencePatchPlan(type, operations,
            mentioned.ToDictionary(m => m.Key, m => (IReadOnlyCollection<Guid>)m.Value, StringComparer.OrdinalIgnoreCase),
            replaced);
    }

    private static ScimReferenceAttribute? Resolve(ScimServiceResourceType type, ScimAttributePath path)
    {
        var schemaUrn = path.SchemaUrn;
        if (schemaUrn != null && !type.Schema.Declares(schemaUrn))
            return null;

        var name = schemaUrn == null || !type.Schema.IsExtension(schemaUrn)
            ? path.AttributeName
            : $"{type.Schema.CanonicalUrn(schemaUrn)}:{path.AttributeName}";
        return type.FindReferenceAttribute(name);
    }

    // A required reference attribute may not be left empty, which only a check of the whole resource can see.
    private static bool IsRequired(ScimServiceResourceType type, ScimReferenceAttribute attribute) =>
        type.Schema.Find(attribute.SchemaUrn, attribute.AttributeName)?.Required ?? true;

    private static bool TryValueEquality(ScimFilter filter, out string literal)
    {
        literal = string.Empty;
        if (filter is not ScimComparisonFilter
            {
                Operator: ScimComparisonOperator.Eq,
                Path: { SchemaUrn: null, SubAttributeName: null } attributePath,
                Value: { Kind: ScimFilterValueKind.String, Text: { } text }
            } || !string.Equals(attributePath.AttributeName, ValueMember, StringComparison.OrdinalIgnoreCase))
            return false;

        literal = text;
        return true;
    }
}
