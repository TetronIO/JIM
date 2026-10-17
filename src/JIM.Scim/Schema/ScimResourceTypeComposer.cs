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
            flattened.AddRange(ScimAttributeMapper.FlattenSchema(baseSchema));
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

            flattened.AddRange(ScimAttributeMapper.FlattenSchema(extensionSchema, prefix));
        }
    }

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
