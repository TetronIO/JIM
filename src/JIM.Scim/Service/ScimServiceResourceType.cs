// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Models.Scim;
using JIM.Scim.Schema;

namespace JIM.Scim.Service;

/// <summary>
/// One resource type a Connected System's store serves, compiled from its stored definitions
/// (<see cref="ScimServiceDefinitions"/>): its schemas, and which of its attributes are unique and which are references.
/// </summary>
public sealed class ScimServiceResourceType
{
    internal ScimServiceResourceType(int id, string name, string endpoint, ScimResourceSchema schema,
        IReadOnlyList<ScimUniqueAttribute> uniqueAttributes, IReadOnlyList<ScimReferenceAttribute> referenceAttributes)
    {
        Id = id;
        Name = name;
        Endpoint = endpoint;
        Schema = schema;
        UniqueAttributes = uniqueAttributes;
        ReferenceAttributes = referenceAttributes;
    }

    /// <summary>
    /// The store's id for the resource type.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// The resource type's name, <c>meta.resourceType</c> on its resources.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The endpoint its resources are served at, with a leading slash (<c>/Users</c>).
    /// </summary>
    public string Endpoint { get; }

    /// <summary>
    /// Its base schema and extensions.
    /// </summary>
    public ScimResourceSchema Schema { get; }

    /// <summary>
    /// The attributes whose values must be unique among its live resources.
    /// </summary>
    public IReadOnlyList<ScimUniqueAttribute> UniqueAttributes { get; }

    /// <summary>
    /// The attributes held as references rather than in the payload.
    /// </summary>
    public IReadOnlyList<ScimReferenceAttribute> ReferenceAttributes { get; }

    /// <summary>
    /// The reference attribute a name denotes (unqualified for the base schema, URN-qualified for an extension), or
    /// null where it is not one.
    /// </summary>
    public ScimReferenceAttribute? FindReferenceAttribute(string name) =>
        ReferenceAttributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The unique values a resource holds, normalised for comparison, one per distinct value of each unique attribute.
    /// </summary>
    public IReadOnlyList<ScimStoreUniqueValue> UniqueValues(JsonObject resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var values = new List<ScimStoreUniqueValue>();
        foreach (var attribute in UniqueAttributes)
        {
            var container = attribute.SchemaUrn == null ? resource : ScimEntryFilter.Member(resource, attribute.SchemaUrn) as JsonObject;
            var member = container == null ? null : ScimEntryFilter.Member(container, attribute.AttributeName);
            // An array, not a collection expression: beside a JsonArray branch, [member] would build a JsonArray and
            // try to re-parent the node.
            IEnumerable<JsonNode?> candidates = member is JsonArray array ? array : new[] { member };

            values.AddRange(candidates
                .Select(attribute.Normalise)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Select(value => new ScimStoreUniqueValue { ResourceTypeId = Id, Attribute = attribute.Name, Value = value }));
        }

        return values;
    }
}
