// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Scim.Discovery;

namespace JIM.Scim.Schema;

/// <summary>
/// The schemas one resource type is made of, as the Service Provider holds it: its base schema, the extensions it
/// declares, and the common attributes every resource carries (RFC 7643 section 3.1). Attributes are found by name
/// without regard to case, as RFC 7643 section 2.1 requires, unqualified names in the base schema and URN-qualified
/// ones in the schema the URN names.
/// <para>
/// This is the server's view of a resource, in SCIM's own terms. The client's flattened attributes
/// (<see cref="ScimFlattenedAttribute"/>) are JIM's mapping of the same schemas onto Connected System attributes, and
/// stay a separate concern.
/// </para>
/// </summary>
public sealed class ScimResourceSchema
{
    private static readonly List<ScimSchemaAttribute> CommonAttributes =
    [
        new() { Name = "id", Type = ScimAttributeTypes.String, Mutability = ScimMutability.ReadOnly, Returned = "always", CaseExact = true },
        new() { Name = "externalId", Type = ScimAttributeTypes.String, Mutability = ScimMutability.ReadWrite, CaseExact = true },
        new()
        {
            Name = "meta",
            Type = ScimAttributeTypes.Complex,
            Mutability = ScimMutability.ReadOnly,
            SubAttributes =
            [
                new() { Name = "resourceType", Type = ScimAttributeTypes.String, Mutability = ScimMutability.ReadOnly, CaseExact = true },
                new() { Name = "created", Type = ScimAttributeTypes.DateTime, Mutability = ScimMutability.ReadOnly },
                new() { Name = "lastModified", Type = ScimAttributeTypes.DateTime, Mutability = ScimMutability.ReadOnly },
                new() { Name = "location", Type = ScimAttributeTypes.Reference, Mutability = ScimMutability.ReadOnly, CaseExact = true },
                new() { Name = "version", Type = ScimAttributeTypes.String, Mutability = ScimMutability.ReadOnly, CaseExact = true }
            ]
        }
    ];

    private readonly Dictionary<string, ScimSchema> _schemasByUrn;

    private ScimResourceSchema(ScimSchema baseSchema, List<ScimSchema> extensions)
    {
        BaseSchemaUrn = baseSchema.Id!;
        ExtensionUrns = extensions.Select(e => e.Id!).ToList();
        _schemasByUrn = new Dictionary<string, ScimSchema>(StringComparer.OrdinalIgnoreCase) { [BaseSchemaUrn] = baseSchema };
        foreach (var extension in extensions)
            _schemasByUrn[extension.Id!] = extension;
    }

    /// <summary>
    /// The resource type's own schema URN, which unqualified attribute names belong to.
    /// </summary>
    public string BaseSchemaUrn { get; }

    /// <summary>
    /// The extension schemas the resource type declares, each held in the resource under a member named by its URN.
    /// </summary>
    public IReadOnlyList<string> ExtensionUrns { get; }

    /// <summary>
    /// Builds the index for a resource type from its base schema and the extensions it declares.
    /// </summary>
    public static ScimResourceSchema For(ScimSchema baseSchema, IEnumerable<ScimSchema> extensions)
    {
        ArgumentNullException.ThrowIfNull(baseSchema);
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseSchema.Id);

        var extensionList = extensions.ToList();
        if (extensionList.Any(e => string.IsNullOrWhiteSpace(e.Id)))
            throw new ArgumentException("Every extension schema needs its URN.", nameof(extensions));

        return new ScimResourceSchema(baseSchema, extensionList);
    }

    /// <summary>
    /// Whether an attribute must never be accepted or returned: <c>mutability</c> <c>writeOnly</c>, or <c>returned</c>
    /// <c>never</c>. <c>password</c> is the core schemas' one such attribute (D5).
    /// </summary>
    public static bool IsWriteOnly(ScimSchemaAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return string.Equals(attribute.Mutability, ScimMutability.WriteOnly, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(attribute.Returned, "never", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a client may never set an attribute: <c>mutability</c> <c>readOnly</c>.
    /// </summary>
    public static bool IsReadOnly(ScimSchemaAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return string.Equals(attribute.Mutability, ScimMutability.ReadOnly, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a URN is the base schema or one of the declared extensions.
    /// </summary>
    public bool Declares(string schemaUrn) => _schemasByUrn.ContainsKey(schemaUrn);

    /// <summary>
    /// Whether a URN is one of the declared extensions, whose attributes live under a member of their own.
    /// </summary>
    public bool IsExtension(string schemaUrn) =>
        Declares(schemaUrn) && !string.Equals(schemaUrn, BaseSchemaUrn, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The declared spelling of a URN, for a member name the client may have spelt in another case.
    /// </summary>
    public string CanonicalUrn(string schemaUrn) => _schemasByUrn.TryGetValue(schemaUrn, out var schema) ? schema.Id! : schemaUrn;

    /// <summary>
    /// A top-level attribute, unqualified (<paramref name="schemaUrn"/> null, or the base URN) from the base schema and
    /// the common attributes, otherwise from the extension the URN names. Null where it is not defined.
    /// </summary>
    public ScimSchemaAttribute? Find(string? schemaUrn, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (schemaUrn == null || string.Equals(schemaUrn, BaseSchemaUrn, StringComparison.OrdinalIgnoreCase))
        {
            return Named(_schemasByUrn[BaseSchemaUrn].Attributes, name) ?? Named(CommonAttributes, name);
        }

        return _schemasByUrn.TryGetValue(schemaUrn, out var schema) ? Named(schema.Attributes, name) : null;
    }

    /// <summary>
    /// A sub-attribute of a complex attribute, found without regard to case.
    /// </summary>
    public static ScimSchemaAttribute? FindSubAttribute(ScimSchemaAttribute complex, string name)
    {
        ArgumentNullException.ThrowIfNull(complex);
        ArgumentNullException.ThrowIfNull(name);
        return Named(complex.SubAttributes, name);
    }

    /// <summary>
    /// Splits a URN-qualified attribute name (<c>urn:...:enterprise:2.0:User:department</c>) into the declared URN and
    /// the attribute name. False where the name carries no declared URN.
    /// </summary>
    public bool TrySplitQualifiedName(string qualified, out string schemaUrn, out string name)
    {
        ArgumentNullException.ThrowIfNull(qualified);

        foreach (var urn in _schemasByUrn.Keys.Where(urn => qualified.Length > urn.Length + 1 &&
                                                             qualified.StartsWith(urn, StringComparison.OrdinalIgnoreCase) &&
                                                             qualified[urn.Length] == ':'))
        {
            schemaUrn = CanonicalUrn(urn);
            name = qualified[(urn.Length + 1)..];
            return true;
        }

        schemaUrn = string.Empty;
        name = string.Empty;
        return false;
    }

    private static ScimSchemaAttribute? Named(IEnumerable<ScimSchemaAttribute> attributes, string name) =>
        attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}
