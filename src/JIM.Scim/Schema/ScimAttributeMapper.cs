// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Scim.Discovery;

namespace JIM.Scim.Schema;

/// <summary>
/// Turns a SCIM schema definition into the flat, typed attribute list JIM works with.
/// <para>
/// Owned by <c>JIM.Scim</c> so the client connector and JIM's own service provider flatten identically;
/// a round trip between the two must not rename or retype anything.
/// </para>
/// <para>
/// The flattening rules, in the order they are applied to an attribute:
/// </para>
/// <list type="number">
/// <item>A complex attribute carrying a <c>$ref</c> sub-attribute is a reference (a manager, a group
/// membership). It becomes a single <see cref="AttributeDataType.Reference"/> attribute keeping the
/// source's plurality, rather than being split into its parts.</item>
/// <item>A multi-valued complex attribute whose <c>type</c> sub-attribute advertises canonical values
/// is flattened per canonical value: <c>emails.work</c>, <c>emails.home</c>, plus <c>emails.primary</c>
/// where the schema has a <c>primary</c> sub-attribute. This yields deterministic single-valued targets
/// an Attribute Flow can be pointed at, which matters more for synchronisation than preserving the raw
/// list shape.</item>
/// <item>Any other complex attribute is flattened per sub-attribute (<c>name.givenName</c>), inheriting
/// the parent's plurality.</item>
/// <item>A simple attribute maps straight across.</item>
/// </list>
/// </summary>
public static class ScimAttributeMapper
{
    /// <summary>The sub-attribute naming a multi-valued entry's canonical type (RFC 7643 section 2.4).</summary>
    private const string TypeSubAttribute = "type";

    /// <summary>The sub-attribute marking the preferred entry of a multi-valued attribute.</summary>
    private const string PrimarySubAttribute = "primary";

    /// <summary>The sub-attribute holding a multi-valued entry's value, where the entry has a single one.</summary>
    private const string ValueSubAttribute = "value";

    /// <summary>The sub-attribute holding the URI of a referenced resource (RFC 7643 section 2.4).</summary>
    private const string RefSubAttribute = "$ref";

    /// <summary>
    /// Flattens every attribute in a schema, skipping unnamed attributes and any whose flattened name
    /// repeats one already emitted.
    /// </summary>
    /// <param name="schema">The schema to flatten.</param>
    /// <param name="namePrefix">
    /// The prefix applied to attribute names when the schema is an extension; see
    /// <see cref="DeriveNamePrefix"/>. Null for a resource type's base schema.
    /// </param>
    public static List<ScimFlattenedAttribute> FlattenSchema(ScimSchema schema, string? namePrefix = null)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var flattened = new List<ScimFlattenedAttribute>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var attribute in schema.Attributes.Where(a => !string.IsNullOrWhiteSpace(a.Name)))
        {
            // A provider repeating an attribute must not produce two Connected System Attributes with
            // the same name; the persistence layer keys on the name.
            foreach (var candidate in Flatten(attribute, schema.Id ?? string.Empty, namePrefix).Where(candidate => seen.Add(candidate.Name)))
                flattened.Add(candidate);
        }

        return flattened;
    }

    /// <summary>
    /// Flattens a single schema attribute into the JIM attributes it contributes.
    /// </summary>
    /// <param name="attribute">The SCIM schema attribute.</param>
    /// <param name="schemaUrn">The URN of the schema that defined it, recorded as the class name.</param>
    /// <param name="namePrefix">The extension prefix, or null for a base schema.</param>
    public static List<ScimFlattenedAttribute> Flatten(ScimSchemaAttribute attribute, string schemaUrn, string? namePrefix = null)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        if (string.IsNullOrWhiteSpace(attribute.Name))
            return [];

        var name = namePrefix == null ? attribute.Name : $"{namePrefix}.{attribute.Name}";

        // Extension attributes are addressed by their URN-qualified name on the wire (RFC 7644 section
        // 3.10), whatever short name JIM shows the administrator.
        var path = namePrefix == null ? attribute.Name : $"{schemaUrn}:{attribute.Name}";

        var extensionUrn = namePrefix == null ? null : schemaUrn;

        if (!IsComplex(attribute))
        {
            return [Build(attribute, name, path, schemaUrn, MapType(attribute.Type), PluralityOf(attribute), attribute.Required, WritabilityOf(attribute),
                ScimValueAccess.Simple, attribute.Name, subAttributeName: null, canonicalType: null, selectsPrimary: false, extensionUrn)];
        }

        // Rule 1: a complex attribute carrying $ref is a reference, not a structure to take apart.
        if (attribute.SubAttributes.Any(s => string.Equals(s.Name, RefSubAttribute, StringComparison.OrdinalIgnoreCase)))
        {
            return [Build(attribute, name, path, schemaUrn, AttributeDataType.Reference, PluralityOf(attribute), attribute.Required, WritabilityOf(attribute),
                ScimValueAccess.ComplexReference, attribute.Name, subAttributeName: null, canonicalType: null, selectsPrimary: false, extensionUrn)];
        }

        // A complex attribute with nothing inside carries no readable or writable leaf. Emitting a bare
        // attribute of type Text would invite an Attribute Flow that can never work.
        var subAttributes = attribute.SubAttributes.Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToList();
        if (subAttributes.Count == 0)
            return [];

        var canonicalValues = CanonicalValuesOf(subAttributes);
        if (attribute.MultiValued && canonicalValues.Count > 0)
            return FlattenByCanonicalType(attribute, subAttributes, canonicalValues, name, path, schemaUrn, extensionUrn);

        // Rules 3 and 4: flatten per sub-attribute, inheriting the parent's plurality. A sub-attribute is
        // only required when the parent is too: a required sub-attribute of an optional complex attribute
        // binds only once the parent is being sent.
        return subAttributes
            .Select(sub => Build(
                sub,
                $"{name}.{sub.Name}",
                $"{path}.{sub.Name}",
                schemaUrn,
                MapType(sub.Type),
                PluralityOf(attribute),
                attribute.Required && sub.Required,
                MostRestrictive(WritabilityOf(attribute), WritabilityOf(sub)),
                ScimValueAccess.ComplexSubAttribute,
                attribute.Name,
                sub.Name,
                canonicalType: null,
                selectsPrimary: false,
                extensionUrn))
            .ToList();
    }

    /// <summary>
    /// Derives the name prefix for an extension schema's attributes, keeping them distinguishable from
    /// identically-named core attributes without exposing a URN in every attribute name.
    /// </summary>
    public static string DeriveNamePrefix(ScimSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var source = !string.IsNullOrWhiteSpace(schema.Name)
            ? schema.Name
            : schema.Id?.Split(':').LastOrDefault(segment => !string.IsNullOrWhiteSpace(segment));

        if (string.IsNullOrWhiteSpace(source))
            return "extension";

        return char.ToLowerInvariant(source[0]) + source[1..];
    }

    /// <summary>
    /// Maps a SCIM data type keyword to its JIM equivalent. An unrecognised type falls back to
    /// <see cref="AttributeDataType.Text"/>: a vendor type JIM does not model is still importable as
    /// text, and dropping the attribute would lose data silently.
    /// </summary>
    public static AttributeDataType MapType(string? scimType)
    {
        if (string.IsNullOrWhiteSpace(scimType))
            return AttributeDataType.Text;

        // RFC 7643 section 2.1: attribute names, and by extension these keywords, are case insensitive;
        // providers differ on "dateTime" versus "datetime".
        return scimType.ToLowerInvariant() switch
        {
            "boolean" => AttributeDataType.Boolean,
            // SCIM integers are not bounded to 32 bits, so Number would silently overflow.
            "integer" => AttributeDataType.LongNumber,
            "decimal" => AttributeDataType.Decimal,
            "datetime" => AttributeDataType.DateTime,
            "binary" => AttributeDataType.Binary,
            "reference" => AttributeDataType.Reference,
            _ => AttributeDataType.Text
        };
    }

    /// <summary>
    /// Flattens a multi-valued complex attribute into one slot per canonical type, plus a primary slot
    /// where the schema models one.
    /// </summary>
    private static List<ScimFlattenedAttribute> FlattenByCanonicalType(
        ScimSchemaAttribute attribute,
        List<ScimSchemaAttribute> subAttributes,
        List<string> canonicalValues,
        string name,
        string path,
        string schemaUrn,
        string? extensionUrn)
    {
        var hasPrimary = subAttributes.Any(s => string.Equals(s.Name, PrimarySubAttribute, StringComparison.OrdinalIgnoreCase));

        // The type and primary sub-attributes are the keys the slots are cut by, so they are not
        // themselves slots.
        var payload = subAttributes
            .Where(s => !string.Equals(s.Name, TypeSubAttribute, StringComparison.OrdinalIgnoreCase))
            .Where(s => !string.Equals(s.Name, PrimarySubAttribute, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var value = payload.SingleOrDefault(s => string.Equals(s.Name, ValueSubAttribute, StringComparison.OrdinalIgnoreCase));

        var selectors = canonicalValues
            .Select(canonical => (Suffix: canonical, Filter: $"[{TypeSubAttribute} eq \"{canonical}\"]", CanonicalType: (string?)canonical, SelectsPrimary: false))
            .ToList();

        if (hasPrimary)
            selectors.Add((PrimarySubAttribute, $"[{PrimarySubAttribute} eq true]", null, true));

        var flattened = new List<ScimFlattenedAttribute>();
        foreach (var (suffix, filter, canonicalType, selectsPrimary) in selectors)
        {
            // Where the entry has a single "value", the slot is the value itself (emails.work). Where it
            // does not, the entry is spread across several sub-attributes and each needs its own slot
            // (addresses.work.streetAddress).
            if (value != null)
            {
                flattened.Add(Build(
                    value,
                    $"{name}.{suffix}",
                    $"{path}{filter}.{value.Name}",
                    schemaUrn,
                    MapType(value.Type),
                    AttributePlurality.SingleValued,
                    // A canonical slot is never required: a provider requires the attribute, never a
                    // particular canonical type within it.
                    required: false,
                    MostRestrictive(WritabilityOf(attribute), WritabilityOf(value)),
                    ScimValueAccess.CanonicalSlot,
                    attribute.Name,
                    value.Name,
                    canonicalType,
                    selectsPrimary,
                    extensionUrn));
                continue;
            }

            flattened.AddRange(payload.Select(sub => Build(
                sub,
                $"{name}.{suffix}.{sub.Name}",
                $"{path}{filter}.{sub.Name}",
                schemaUrn,
                MapType(sub.Type),
                AttributePlurality.SingleValued,
                required: false,
                MostRestrictive(WritabilityOf(attribute), WritabilityOf(sub)),
                ScimValueAccess.CanonicalSlot,
                attribute.Name,
                sub.Name,
                canonicalType,
                selectsPrimary,
                extensionUrn)));
        }

        return flattened;
    }

    private static ScimFlattenedAttribute Build(
        ScimSchemaAttribute source,
        string name,
        string path,
        string schemaUrn,
        AttributeDataType type,
        AttributePlurality plurality,
        bool required,
        AttributeWritability writability,
        ScimValueAccess access,
        string? sourceAttributeName,
        string? subAttributeName,
        string? canonicalType,
        bool selectsPrimary,
        string? extensionUrn)
    {
        return new ScimFlattenedAttribute(name, path, type, plurality, required, writability, schemaUrn, source.Description,
            access, sourceAttributeName, subAttributeName, canonicalType, selectsPrimary, extensionUrn);
    }

    private static bool IsComplex(ScimSchemaAttribute attribute)
    {
        return string.Equals(attribute.Type, ScimAttributeTypes.Complex, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> CanonicalValuesOf(List<ScimSchemaAttribute> subAttributes)
    {
        var typeSubAttribute = subAttributes.FirstOrDefault(s => string.Equals(s.Name, TypeSubAttribute, StringComparison.OrdinalIgnoreCase));
        return typeSubAttribute?.CanonicalValues.Where(v => !string.IsNullOrWhiteSpace(v)).ToList() ?? [];
    }

    private static AttributePlurality PluralityOf(ScimSchemaAttribute attribute)
    {
        return attribute.MultiValued ? AttributePlurality.MultiValued : AttributePlurality.SingleValued;
    }

    /// <summary>
    /// RFC 7643 section 7 defaults mutability to readWrite, so an absent value means writable. Only
    /// readOnly is genuinely unwritable; immutable and writeOnly can both be set on export.
    /// </summary>
    private static AttributeWritability WritabilityOf(ScimSchemaAttribute attribute)
    {
        return string.Equals(attribute.Mutability, ScimMutability.ReadOnly, StringComparison.OrdinalIgnoreCase)
            ? AttributeWritability.ReadOnly
            : AttributeWritability.Writable;
    }

    private static AttributeWritability MostRestrictive(AttributeWritability parent, AttributeWritability child)
    {
        return parent == AttributeWritability.ReadOnly || child == AttributeWritability.ReadOnly
            ? AttributeWritability.ReadOnly
            : AttributeWritability.Writable;
    }
}
