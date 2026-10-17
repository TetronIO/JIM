// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Scim.Discovery;
using JIM.Utilities;

namespace JIM.Scim.Schema;

/// <summary>
/// Derives the flattened attributes of each resource type from its base schema, its schema extensions and the common
/// attributes every SCIM resource carries.
/// <para>
/// One composer for both directions: the client connector composes a provider's published documents through it, and
/// JIM's own service provider publishes documents that compose to exactly the attributes it reads and writes, so an
/// attribute is named identically on either side (<c>emails.work</c>, <c>enterpriseUser.department</c>).
/// </para>
/// </summary>
public static class ScimResourceTypeComposer
{
    /// <summary>
    /// Composes every named resource type. A schema the provider did not publish falls back to the RFC's own
    /// definition where JIM has one; a gap that leaves attributes unavailable is reported as a warning rather than
    /// hiding the resource type, since the provider does advertise it.
    /// </summary>
    public static ScimResourceTypeComposition Compose(IEnumerable<ScimResourceType> resourceTypes, IEnumerable<ScimSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(resourceTypes);
        ArgumentNullException.ThrowIfNull(schemas);

        var schemasByUrn = BuildSchemaIndex(schemas);
        var warnings = new List<string>();
        var flattenedAttributes = new Dictionary<string, List<ScimFlattenedAttribute>>(StringComparer.OrdinalIgnoreCase);

        foreach (var resourceType in resourceTypes.Where(r => !string.IsNullOrWhiteSpace(r.Name)))
            flattenedAttributes[resourceType.Name!] = BuildFlattenedAttributes(resourceType, schemasByUrn, warnings);

        return new ScimResourceTypeComposition { FlattenedAttributes = flattenedAttributes, Warnings = warnings };
    }

    /// <summary>
    /// Flattens everything a resource type is composed of: its base schema, its extensions and the
    /// common attributes every SCIM resource carries. Names repeat at most once.
    /// </summary>
    private static List<ScimFlattenedAttribute> BuildFlattenedAttributes(
        ScimResourceType resourceType,
        Dictionary<string, ScimSchema> schemasByUrn,
        List<string> warnings)
    {
        var flattened = ScimCommonAttributes.For(resourceType.Schema ?? string.Empty);

        var baseSchema = ResolveSchema(resourceType.Schema, schemasByUrn);
        if (baseSchema == null)
        {
            // Keep the object type: the provider advertises it, and hiding it would make a provider gap
            // look like a JIM one. The common attributes still make it importable.
            warnings.Add($"The service provider advertises the {LogSanitiser.Sanitise(resourceType.Name)} resource type but published no schema for it, " +
                         "so only the standard SCIM attributes are available. Attributes specific to this resource type cannot be synchronised.");
        }
        else
        {
            flattened.AddRange(ScimAttributeMapper.FlattenSchema(WithCoreSubAttributes(baseSchema, resourceType.Name, warnings)));
        }

        AddExtensionAttributes(resourceType, schemasByUrn, flattened, warnings);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return flattened.Where(a => seen.Add(a.Name)).ToList();
    }

    private static void AddExtensionAttributes(
        ScimResourceType resourceType,
        Dictionary<string, ScimSchema> schemasByUrn,
        List<ScimFlattenedAttribute> flattened,
        List<string> warnings)
    {
        var usedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var extension in resourceType.SchemaExtensions.Where(e => !string.IsNullOrWhiteSpace(e.Schema)))
        {
            var extensionSchema = ResolveSchema(extension.Schema, schemasByUrn);
            if (extensionSchema == null)
            {
                warnings.Add($"The {LogSanitiser.Sanitise(resourceType.Name)} resource type declares the schema extension " +
                             $"{LogSanitiser.Sanitise(extension.Schema)}, but the provider published no definition for it, so its attributes are unavailable.");
                continue;
            }

            // Two vendors naming their extension the same must not collapse into one set of attributes,
            // so the loser of a prefix collision is addressed by its URN instead.
            var prefix = ScimAttributeMapper.DeriveNamePrefix(extensionSchema);
            if (!usedPrefixes.Add(prefix))
                prefix = extension.Schema!;

            flattened.AddRange(ScimAttributeMapper.FlattenSchema(WithCoreSubAttributes(extensionSchema, resourceType.Name, warnings), prefix));
        }
    }

    /// <summary>
    /// Fills in the sub-attributes of a core attribute the provider published as complex with nothing inside (#2094).
    /// <para>
    /// Keycloak publishes a Group's <c>members</c>, and a User's <c>groups</c> and <c>emails</c>, this way. A complex
    /// attribute with no sub-attributes flattens to nothing, so the Connected System would have no membership to import
    /// or export at all. RFC 7643 defines these attributes, so its definition supplies what is inside them, while
    /// everything the provider did say about the attribute (whether it is multi-valued, writable, returned only on
    /// request) is kept. An attribute JIM has no definition for is left as it is; reported, so the gap reads as the
    /// provider's.
    /// </para>
    /// </summary>
    private static ScimSchema WithCoreSubAttributes(ScimSchema schema, string? resourceTypeName, List<string> warnings)
    {
        var core = ScimCoreSchemas.ByUrn(schema.Id);
        if (core == null || !schema.Attributes.Any(IsEmptyComplex))
            return schema;

        var filled = new List<string>();
        var attributes = schema.Attributes.Select(attribute =>
        {
            var definition = IsEmptyComplex(attribute)
                ? core.Attributes.FirstOrDefault(c => string.Equals(c.Name, attribute.Name, StringComparison.OrdinalIgnoreCase) && c.SubAttributes.Count > 0)
                : null;
            if (definition == null)
                return attribute;

            filled.Add(attribute.Name!);
            return WithSubAttributes(attribute, definition.SubAttributes);
        }).ToList();

        if (filled.Count > 0)
        {
            warnings.Add($"The service provider's schema for the {LogSanitiser.Sanitise(resourceTypeName)} resource type defines " +
                         $"{string.Join(", ", filled.Select(name => $"'{LogSanitiser.Sanitise(name)}'"))} without saying what is inside, " +
                         "so JIM used the RFC 7643 definition of what is inside, keeping everything else the provider said about it.");
        }

        return new ScimSchema { Id = schema.Id, Name = schema.Name, Description = schema.Description, Attributes = attributes, Meta = schema.Meta };
    }

    private static bool IsEmptyComplex(ScimSchemaAttribute attribute) =>
        string.Equals(attribute.Type, ScimAttributeTypes.Complex, StringComparison.OrdinalIgnoreCase)
        && attribute.SubAttributes.All(s => string.IsNullOrWhiteSpace(s.Name));

    /// <summary>A copy of the provider's attribute with the given sub-attributes, so the provider's own documents are not changed.</summary>
    private static ScimSchemaAttribute WithSubAttributes(ScimSchemaAttribute attribute, List<ScimSchemaAttribute> subAttributes) => new()
    {
        Name = attribute.Name,
        Type = attribute.Type,
        Description = attribute.Description,
        SubAttributes = subAttributes,
        MultiValued = attribute.MultiValued,
        Required = attribute.Required,
        CanonicalValues = attribute.CanonicalValues,
        CaseExact = attribute.CaseExact,
        Mutability = attribute.Mutability,
        Returned = attribute.Returned,
        Uniqueness = attribute.Uniqueness,
        ReferenceTypes = attribute.ReferenceTypes
    };

    /// <summary>
    /// Finds a schema in the provider's published set, falling back to the RFC's own definition when the
    /// provider did not publish one for a schema JIM knows.
    /// </summary>
    private static ScimSchema? ResolveSchema(string? urn, Dictionary<string, ScimSchema> schemasByUrn)
    {
        if (string.IsNullOrWhiteSpace(urn))
            return null;

        return schemasByUrn.TryGetValue(urn, out var schema) ? schema : ScimCoreSchemas.ByUrn(urn);
    }

    private static Dictionary<string, ScimSchema> BuildSchemaIndex(IEnumerable<ScimSchema> schemas)
    {
        var index = new Dictionary<string, ScimSchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in schemas.Where(s => !string.IsNullOrWhiteSpace(s.Id)))
            index.TryAdd(schema.Id!, schema);

        return index;
    }
}
