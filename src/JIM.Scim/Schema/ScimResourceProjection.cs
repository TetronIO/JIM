// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Scim.Discovery;
using JIM.Scim.Resources;

namespace JIM.Scim.Schema;

/// <summary>
/// What a response carries for a stored resource: its attributes with the Service Provider's <c>schemas</c>, <c>id</c>
/// and <c>meta</c> added (RFC 7643 section 3.1), honouring each attribute's <c>returned</c> characteristic and a request's
/// <c>attributes</c> or <c>excludedAttributes</c> (RFC 7644 section 3.9).
/// <list type="bullet">
/// <item>A write-only attribute, or one <c>returned</c> <c>never</c>, is never returned, even when asked for (D5).</item>
/// <item><c>returned</c> <c>always</c> (<c>id</c>) is returned whatever the request says.</item>
/// <item><c>returned</c> <c>request</c> is returned only when <c>attributes</c> names it.</item>
/// </list>
/// Names in either list may be an attribute, a sub-attribute (<c>name.givenName</c>), an extension's attribute qualified
/// by its URN, or an extension's URN alone for all of it; a name the resource type does not define selects nothing.
/// </summary>
public static class ScimResourceProjection
{
    private const string SchemasMember = "schemas";
    private const string IdAttribute = "id";
    private const string MetaAttribute = "meta";

    /// <summary>
    /// Builds the response body for one resource. The stored resource is never changed.
    /// </summary>
    /// <param name="stored">The attributes the store holds, as <see cref="ScimResourceIntake"/> and <see cref="ScimPatchApplier"/> left them.</param>
    /// <param name="metadata">The server's <c>id</c> and <c>meta</c> for the resource.</param>
    /// <param name="schema">The resource type's schemas.</param>
    /// <param name="attributes">The request's <c>attributes</c>, if any.</param>
    /// <param name="excludedAttributes">The request's <c>excludedAttributes</c>, if any.</param>
    /// <exception cref="ArgumentException">Both lists were given, which RFC 7644 section 3.9 does not allow.</exception>
    public static JsonObject Project(
        JsonObject stored,
        ScimResourceMetadata metadata,
        ScimResourceSchema schema,
        IReadOnlyCollection<string>? attributes = null,
        IReadOnlyCollection<string>? excludedAttributes = null)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(schema);

        if (attributes is { Count: > 0 } && excludedAttributes is { Count: > 0 })
            throw new ArgumentException("A request may give attributes or excludedAttributes, not both.", nameof(excludedAttributes));

        var selection = new Selection(
            schema,
            attributes is { Count: > 0 } ? Parse(attributes, schema) : null,
            excludedAttributes is { Count: > 0 } ? Parse(excludedAttributes, schema) : []);

        var body = new JsonObject();
        foreach (var (name, value) in stored.Where(member => !IsServerStated(member.Key)))
        {
            if (schema.IsExtension(name))
            {
                if (value is not JsonObject extension)
                    continue;

                var urn = schema.CanonicalUrn(name);
                var projected = new JsonObject();
                foreach (var (memberName, memberValue) in extension)
                    Add(projected, schema.Find(urn, memberName), urn, memberValue, selection);

                if (projected.Count > 0)
                    body[urn] = projected;
                continue;
            }

            Add(body, schema.Find(null, name), null, value, selection);
        }

        var response = new JsonObject
        {
            [SchemasMember] = new JsonArray(
                [JsonValue.Create(schema.BaseSchemaUrn), .. schema.ExtensionUrns.Where(body.ContainsKey).Select(urn => (JsonNode?)JsonValue.Create(urn))])
        };

        Add(response, schema.Find(null, IdAttribute), null, JsonValue.Create(metadata.Id), selection);
        foreach (var (name, value) in body.ToList())
        {
            body.Remove(name);
            response[name] = value;
        }

        Add(response, schema.Find(null, MetaAttribute), null, Meta(metadata), selection);
        return response;
    }

    private static bool IsServerStated(string name) =>
        string.Equals(name, SchemasMember, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, IdAttribute, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, MetaAttribute, StringComparison.OrdinalIgnoreCase);

    private static JsonObject Meta(ScimResourceMetadata metadata)
    {
        var meta = new JsonObject
        {
            ["resourceType"] = metadata.ResourceType,
            ["created"] = ScimValueFormatter.ToText(metadata.Created),
            ["lastModified"] = ScimValueFormatter.ToText(metadata.LastModified)
        };

        if (metadata.Location != null)
            meta["location"] = metadata.Location;

        meta["version"] = ScimETag.For(metadata.Version);
        return meta;
    }

    /// <summary>
    /// Adds one attribute to the response, filtered to what the request and the schema allow. An attribute the schema
    /// does not define is left out: the store holds nothing else, and nothing undefined is ever returned.
    /// </summary>
    private static void Add(JsonObject target, ScimSchemaAttribute? attribute, string? schemaUrn, JsonNode? value, Selection selection)
    {
        if (attribute == null || value == null || ScimResourceSchema.IsWriteOnly(attribute))
            return;

        var subs = selection.SubAttributes(attribute, schemaUrn);
        if (subs == null)
            return;

        var projected = attribute.SubAttributes.Count == 0 ? value.DeepClone() : Filter(value, attribute, subs);
        if (projected != null)
            target[attribute.Name!] = projected;
    }

    /// <summary>
    /// A complex value, or each entry of a multi-valued one, cut to the sub-attributes the response carries.
    /// </summary>
    private static JsonNode? Filter(JsonNode value, ScimSchemaAttribute attribute, Func<ScimSchemaAttribute, bool> include)
    {
        switch (value)
        {
            case JsonArray entries:
                var kept = entries.Select(entry => entry == null ? null : Filter(entry, attribute, include)).Where(entry => entry != null).ToArray();
                return kept.Length == 0 ? null : new JsonArray(kept);

            case JsonObject complex:
                var result = new JsonObject();
                var carried = complex
                    .Select(member => (Sub: ScimResourceSchema.FindSubAttribute(attribute, member.Key), member.Value))
                    .Where(member => member.Sub != null && member.Value != null && !ScimResourceSchema.IsWriteOnly(member.Sub) && include(member.Sub));
                foreach (var (sub, member) in carried)
                    result[sub!.Name!] = member!.DeepClone();
                return result.Count == 0 ? null : result;

            default:
                return value.DeepClone();
        }
    }

    private static List<ProjectionPath> Parse(IEnumerable<string> names, ScimResourceSchema schema)
    {
        var paths = new List<ProjectionPath>();
        foreach (var name in names.Select(n => n.Trim()).Where(n => n.Length > 0))
        {
            if (schema.Declares(name))
            {
                paths.Add(new ProjectionPath(schema.IsExtension(name) ? schema.CanonicalUrn(name) : null, null, null));
                continue;
            }

            string? urn = null;
            var rest = name;
            if (schema.TrySplitQualifiedName(name, out var qualifiedUrn, out var qualifiedName))
            {
                urn = schema.IsExtension(qualifiedUrn) ? qualifiedUrn : null;
                rest = qualifiedName;
            }

            var dot = rest.IndexOf('.', StringComparison.Ordinal);
            paths.Add(dot < 0
                ? new ProjectionPath(urn, rest, null)
                : new ProjectionPath(urn, rest[..dot], rest[(dot + 1)..]));
        }

        return paths;
    }

    /// <summary>
    /// One name from <c>attributes</c> or <c>excludedAttributes</c>: a whole schema (<see cref="Attribute"/> null), an
    /// attribute, or one of its sub-attributes. <see cref="SchemaUrn"/> is null for the base schema.
    /// </summary>
    private sealed record ProjectionPath(string? SchemaUrn, string? Attribute, string? SubAttribute);

    /// <summary>
    /// Which attributes and sub-attributes a response carries.
    /// </summary>
    private sealed class Selection(ScimResourceSchema schema, List<ProjectionPath>? included, List<ProjectionPath> excluded)
    {
        /// <summary>
        /// Null where the attribute is left out, otherwise which of its sub-attributes to carry.
        /// </summary>
        public Func<ScimSchemaAttribute, bool>? SubAttributes(ScimSchemaAttribute attribute, string? schemaUrn)
        {
            var always = IsReturned(attribute, "always");
            var named = Naming(included ?? [], attribute, schemaUrn).ToList();
            var namedWhole = named.Any(path => path.SubAttribute == null);
            var namedSubs = named.Where(path => path.SubAttribute != null).Select(path => path.SubAttribute!).ToList();

            if (included != null)
            {
                if (always || namedWhole)
                    return sub => Returns(sub, requested: namedWhole);
                if (namedSubs.Count == 0)
                    return null;
                return sub => IsReturned(sub, "always") || namedSubs.Contains(sub.Name!, StringComparer.OrdinalIgnoreCase);
            }

            if (IsReturned(attribute, "request"))
                return null;

            var excludedPaths = Naming(excluded, attribute, schemaUrn).ToList();
            if (!always && excludedPaths.Any(path => path.SubAttribute == null))
                return null;

            var excludedSubs = excludedPaths.Where(path => path.SubAttribute != null).Select(path => path.SubAttribute!).ToList();
            return sub => Returns(sub, requested: false) &&
                          (IsReturned(sub, "always") || !excludedSubs.Contains(sub.Name!, StringComparer.OrdinalIgnoreCase));
        }

        private IEnumerable<ProjectionPath> Naming(List<ProjectionPath> paths, ScimSchemaAttribute attribute, string? schemaUrn) =>
            paths.Where(path => SameSchema(path.SchemaUrn, schemaUrn) &&
                                (path.Attribute == null ? schemaUrn != null : string.Equals(path.Attribute, attribute.Name, StringComparison.OrdinalIgnoreCase)))
                 .Select(path => path.Attribute == null ? path with { Attribute = attribute.Name } : path);

        private bool SameSchema(string? left, string? right) =>
            left == null ? right == null : right != null && string.Equals(schema.CanonicalUrn(left), right, StringComparison.OrdinalIgnoreCase);

        private static bool Returns(ScimSchemaAttribute sub, bool requested) =>
            !IsReturned(sub, "never") && (requested || !IsReturned(sub, "request"));

        private static bool IsReturned(ScimSchemaAttribute attribute, string returned) =>
            string.Equals(attribute.Returned, returned, StringComparison.OrdinalIgnoreCase);
    }
}
