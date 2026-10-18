// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Models.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Schema;
using JIM.Scim.Serialisation;
using JIM.Utilities;

namespace JIM.Scim.Service;

/// <summary>
/// The resource types one Connected System's store serves, compiled from its stored definitions (D18). Every rule the
/// server applies (intake, PATCH, projection, uniqueness, references) comes from here, so a custom resource type or
/// extension is served by the same code as User and Group.
/// </summary>
public sealed class ScimServiceDefinitions
{
    private const string UniquenessServer = "server";
    private const string UniquenessGlobal = "global";
    private const string ReferenceSubAttribute = "$ref";

    private readonly IReadOnlyList<ScimServiceResourceType> _resourceTypes;

    private ScimServiceDefinitions(IReadOnlyList<ScimServiceResourceType> resourceTypes)
    {
        _resourceTypes = resourceTypes;
    }

    /// <summary>
    /// Every resource type served.
    /// </summary>
    public IReadOnlyList<ScimServiceResourceType> ResourceTypes => _resourceTypes;

    /// <summary>
    /// The resource type served at an endpoint, matched without regard to case or a leading slash; null where none is.
    /// </summary>
    public ScimServiceResourceType? FindByEndpoint(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var wanted = endpoint.Trim('/');
        return _resourceTypes.FirstOrDefault(t => string.Equals(t.Endpoint.Trim('/'), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The resource type with a store id; null where none has it.
    /// </summary>
    public ScimServiceResourceType? FindById(int id) => _resourceTypes.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// The resource type with a name, matched without regard to case; null where none has it.
    /// </summary>
    public ScimServiceResourceType? FindByName(string name) =>
        _resourceTypes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Compiles a Connected System's stored definitions.
    /// </summary>
    /// <exception cref="InvalidOperationException">A resource type names a schema the store does not hold, or a definition
    /// is not a valid RFC 7643 document. The definitions are the store's own configuration, so this is a fault to fail on,
    /// never one to serve around.</exception>
    public static ScimServiceDefinitions Compile(IEnumerable<ScimStoreResourceType> resourceTypes, IEnumerable<ScimStoreSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(resourceTypes);
        ArgumentNullException.ThrowIfNull(schemas);

        var schemasByUrn = schemas.ToDictionary(s => s.Urn, s => Deserialise<ScimSchema>(s.Definition, s.Urn), StringComparer.OrdinalIgnoreCase);
        var typeRows = resourceTypes.ToList();
        var typeNames = typeRows.Select(t => t.Name).ToList();

        var compiled = typeRows.Select(row =>
        {
            var definition = Deserialise<ScimResourceType>(row.Definition, row.Name);
            var baseSchema = Resolve(schemasByUrn, definition.Schema, row.Name);
            var extensions = definition.SchemaExtensions.Select(e => Resolve(schemasByUrn, e.Schema, row.Name)).ToList();
            var schema = ScimResourceSchema.For(baseSchema, extensions);

            var attributesBySchema = new List<(string? Urn, IReadOnlyList<ScimSchemaAttribute> Attributes)> { (null, baseSchema.Attributes) };
            foreach (var extension in extensions)
                attributesBySchema.Add((extension.Id, extension.Attributes));

            var unique = attributesBySchema
                .SelectMany(s => s.Attributes.Where(IsUnique).Select(a => new ScimUniqueAttribute(Qualify(s.Urn, a.Name!), s.Urn, a.Name!, a.CaseExact)))
                .Append(new ScimUniqueAttribute("externalId", null, "externalId", CaseExact: true))
                .ToList();

            var references = attributesBySchema
                .SelectMany(s => s.Attributes.Select(a => (s.Urn, Attribute: a, Targets: ReferenceTargets(a, typeNames))))
                .Where(r => r.Targets.Count > 0)
                .Select(r => new ScimReferenceAttribute(Qualify(r.Urn, r.Attribute.Name!), r.Urn, r.Attribute.Name!, r.Attribute.MultiValued, r.Targets))
                .ToList();

            return new ScimServiceResourceType(row.Id, row.Name, row.Endpoint, schema, unique, references);
        }).ToList();

        return new ScimServiceDefinitions(compiled);
    }

    private static bool IsUnique(ScimSchemaAttribute attribute) =>
        !string.Equals(attribute.Type, ScimAttributeTypes.Complex, StringComparison.OrdinalIgnoreCase) &&
        !ScimResourceSchema.IsWriteOnly(attribute) &&
        (string.Equals(attribute.Uniqueness, UniquenessServer, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(attribute.Uniqueness, UniquenessGlobal, StringComparison.OrdinalIgnoreCase));

    // A writable complex attribute with a $ref naming resource types of this store; read-only ones (a User's groups)
    // are the server's to derive, never a client's to write.
    private static List<string> ReferenceTargets(ScimSchemaAttribute attribute, IReadOnlyList<string> typeNames)
    {
        if (!string.Equals(attribute.Type, ScimAttributeTypes.Complex, StringComparison.OrdinalIgnoreCase) ||
            ScimResourceSchema.IsReadOnly(attribute) || ScimResourceSchema.IsWriteOnly(attribute))
            return [];

        var reference = ScimResourceSchema.FindSubAttribute(attribute, ReferenceSubAttribute);
        if (reference == null)
            return [];

        return typeNames.Where(name => reference.ReferenceTypes.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private static string Qualify(string? schemaUrn, string name) => schemaUrn == null ? name : $"{schemaUrn}:{name}";

    private static ScimSchema Resolve(Dictionary<string, ScimSchema> schemasByUrn, string? urn, string resourceTypeName)
    {
        if (urn != null && schemasByUrn.TryGetValue(urn, out var schema))
            return schema;

        throw new InvalidOperationException(
            $"The SCIM resource type '{LogSanitiser.Sanitise(resourceTypeName)}' names the schema '{LogSanitiser.Sanitise(urn)}', which this Connected System's store does not hold.");
    }

    private static T Deserialise<T>(string definition, string name) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(definition, ScimJson.Options)
                   ?? throw new InvalidOperationException($"The SCIM definition '{LogSanitiser.Sanitise(name)}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The SCIM definition '{LogSanitiser.Sanitise(name)}' is not a valid RFC 7643 document.", ex);
        }
    }
}
