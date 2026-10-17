// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using JIM.Scim.Messages;
using JIM.Scim.Schema;

namespace JIM.Scim.Service;

/// <summary>
/// Moves reference attributes between the two shapes a resource has (D18): in the store they are reference rows beside
/// the payload, and everywhere else (intake, PATCH, responses) they are ordinary attribute values. An entry is
/// <c>{"value": id, "type": typeName}</c>: the identity of the resource referred to and its type, which is everything
/// the store keeps of a reference.
/// </summary>
public static class ScimReferenceEntries
{
    private const string ValueMember = "value";
    private const string TypeMember = "type";

    /// <summary>
    /// The text a reference's id is written as.
    /// </summary>
    public static string Format(Guid id) => id.ToString("D");

    /// <summary>
    /// One reference as an attribute value.
    /// </summary>
    public static JsonObject Entry(ScimHeldReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new JsonObject { [ValueMember] = Format(reference.TargetId), [TypeMember] = reference.TargetTypeName };
    }

    /// <summary>
    /// A resource with its references put back as attribute values: a copy of <paramref name="payload"/> with each
    /// reference attribute that holds any, multi-valued ones as arrays and single-valued ones as one entry, inside their
    /// extension where they belong to one.
    /// </summary>
    /// <param name="references">The references held, keyed by the attribute's store name.</param>
    public static JsonObject Compose(JsonObject payload, ScimServiceResourceType type,
        IReadOnlyDictionary<string, IReadOnlyList<ScimHeldReference>> references)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(references);

        var resource = (JsonObject)payload.DeepClone();
        foreach (var attribute in type.ReferenceAttributes)
        {
            if (!references.TryGetValue(attribute.Name, out var held) || held.Count == 0)
                continue;

            var container = Container(resource, attribute.SchemaUrn);
            container[attribute.AttributeName] = attribute.MultiValued
                ? new JsonArray(held.Select(r => (JsonNode)Entry(r)).ToArray())
                : Entry(held[0]);
        }

        return resource;
    }

    /// <summary>
    /// Divides a resource the way the store holds it: a copy without its reference attributes (an extension left empty
    /// is dropped with them), and the resources each refers to. A value that is not a resource id refuses the resource
    /// (D9); duplicates are one reference.
    /// </summary>
    public static ScimReferenceSplit Split(JsonObject resource, ScimServiceResourceType type)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(type);

        var payload = (JsonObject)resource.DeepClone();
        var references = new Dictionary<string, IReadOnlyList<Guid>>(StringComparer.OrdinalIgnoreCase);

        foreach (var attribute in type.ReferenceAttributes)
        {
            var containerKey = attribute.SchemaUrn == null ? null : ScimEntryFilter.Key(payload, attribute.SchemaUrn);
            var container = attribute.SchemaUrn == null ? payload : containerKey == null ? null : payload[containerKey] as JsonObject;
            var key = container == null ? null : ScimEntryFilter.Key(container, attribute.AttributeName);

            var ids = new List<Guid>();
            if (key != null)
            {
                IEnumerable<JsonNode?> entries = container![key] is JsonArray array ? array : new[] { container[key] };
                foreach (var entry in entries)
                {
                    if (!TryId(entry, out var id))
                    {
                        return new ScimReferenceSplit
                        {
                            Error = ScimError.ForStatus(400, $"The attribute '{attribute.AttributeName}' refers to a resource that does not exist.",
                                ScimErrorTypes.InvalidValue)
                        };
                    }

                    if (!ids.Contains(id))
                        ids.Add(id);
                }

                container.Remove(key);
                if (containerKey != null && container.Count == 0)
                    payload.Remove(containerKey);
            }

            references[attribute.Name] = ids;
        }

        return new ScimReferenceSplit { Payload = payload, References = references };
    }

    /// <summary>
    /// The resource id an entry refers to: its <c>value</c>, which must be a resource id.
    /// </summary>
    internal static bool TryId(JsonNode? entry, out Guid id)
    {
        id = Guid.Empty;
        var value = entry is JsonObject members ? ScimEntryFilter.Member(members, ValueMember) : null;
        return value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String && Guid.TryParse(scalar.GetValue<string>(), out id);
    }

    private static JsonObject Container(JsonObject resource, string? schemaUrn)
    {
        if (schemaUrn == null)
            return resource;

        var key = ScimEntryFilter.Key(resource, schemaUrn);
        if (key != null && resource[key] is JsonObject extension)
            return extension;

        var created = new JsonObject();
        resource[schemaUrn] = created;
        return created;
    }
}
