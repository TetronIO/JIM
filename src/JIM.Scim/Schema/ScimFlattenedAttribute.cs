// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Scim.Schema;

/// <summary>
/// One flat JIM-facing attribute derived from a SCIM schema attribute, together with everything needed
/// to find its value inside a resource.
/// <para>
/// The names differ on purpose. <see cref="Name"/> is what an administrator sees and points an Attribute
/// Flow at, so it is short and dotted; <see cref="ScimPath"/> is the wire form, which for an extension
/// attribute is URN-qualified and for a canonically-typed entry is a value filter. The accessor members
/// below describe the same route structurally, so reading a resource is a lookup rather than a re-parse
/// of the path string.
/// </para>
/// </summary>
public class ScimFlattenedAttribute
{
    /// <summary>
    /// The Connected System Attribute name, for example <c>name.givenName</c>, <c>emails.work</c> or
    /// <c>enterpriseUser.department</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The SCIM attribute path used when reading or writing the value, for example
    /// <c>emails[type eq "work"].value</c>.
    /// </summary>
    public string ScimPath { get; }

    public AttributeDataType Type { get; }

    public AttributePlurality AttributePlurality { get; }

    public bool Required { get; }

    public AttributeWritability Writability { get; }

    /// <summary>
    /// The URN of the schema that defined the attribute, surfaced to administrators so they can see
    /// which core or extension schema an attribute belongs to.
    /// </summary>
    public string ClassName { get; }

    public string? Description { get; }

    /// <summary>How the value is reached inside a resource.</summary>
    public ScimValueAccess Access { get; }

    /// <summary>
    /// The name of the SCIM attribute the value lives under, for example <c>emails</c> or <c>name</c>.
    /// </summary>
    public string SourceAttributeName { get; }

    /// <summary>
    /// The sub-attribute to read, for <see cref="ScimValueAccess.ComplexSubAttribute"/> and
    /// <see cref="ScimValueAccess.CanonicalSlot"/>.
    /// </summary>
    public string? SubAttributeName { get; }

    /// <summary>
    /// The canonical type selecting the entry, for example <c>work</c>. Null when the slot selects the
    /// primary entry instead.
    /// </summary>
    public string? CanonicalType { get; }

    /// <summary>Whether the slot selects the entry flagged primary rather than one with a canonical type.</summary>
    public bool SelectsPrimary { get; }

    /// <summary>
    /// The extension schema URN the attribute sits under on a resource, or null for a base-schema
    /// attribute. Extension attributes are nested inside a JSON member named by their URN
    /// (RFC 7643 section 3), so reading one means descending into that member first.
    /// </summary>
    public string? ExtensionUrn { get; }

    /// <summary>
    /// Whether the provider's schema declares the attribute's values case-exact (RFC 7643 section 7,
    /// <c>caseExact</c>). For a flattened leaf this is the leaf sub-attribute's declaration.
    /// </summary>
    public bool CaseExact { get; }

    /// <summary>
    /// Whether each value is a whole entry of a multi-valued complex attribute without canonical types, identified by
    /// its <see cref="SubAttributeName"/> (<c>roles.value</c>). Such a value is written, added and removed as an entry
    /// of the parent attribute, the way a reference is, never at the sub-attribute's own path, which would reach every
    /// entry at once (#2090).
    /// </summary>
    /// <summary>
    /// Whether the provider's schema makes the attribute write-only: <c>mutability</c> <c>writeOnly</c> or
    /// <c>returned</c> <c>never</c>, on the attribute or on the complex attribute containing it (RFC 7643 section 7).
    /// The core User's <c>password</c> is the one every provider has. An export never writes such an attribute and a
    /// response never returns one (#2091): a password reaches a service provider only through Password
    /// Synchronisation, and a value the provider never returns could never be confirmed by import.
    /// </summary>
    public bool IsWriteOnly { get; }

    public bool IsEntryValue => Access == ScimValueAccess.ComplexSubAttribute && AttributePlurality == AttributePlurality.MultiValued;

    public ScimFlattenedAttribute(
        string name,
        string scimPath,
        AttributeDataType type,
        AttributePlurality attributePlurality,
        bool required,
        AttributeWritability writability,
        string className,
        string? description = null,
        ScimValueAccess access = ScimValueAccess.Simple,
        string? sourceAttributeName = null,
        string? subAttributeName = null,
        string? canonicalType = null,
        bool selectsPrimary = false,
        string? extensionUrn = null,
        bool caseExact = false,
        bool isWriteOnly = false)
    {
        Name = name;
        ScimPath = scimPath;
        Type = type;
        AttributePlurality = attributePlurality;
        Required = required;
        Writability = writability;
        ClassName = className;
        Description = description;
        Access = access;
        SourceAttributeName = sourceAttributeName ?? name;
        SubAttributeName = subAttributeName;
        CanonicalType = canonicalType;
        SelectsPrimary = selectsPrimary;
        ExtensionUrn = extensionUrn;
        CaseExact = caseExact;
        IsWriteOnly = isWriteOnly;
    }

    public ConnectorSchemaAttribute ToConnectorSchemaAttribute()
    {
        return new ConnectorSchemaAttribute(Name, Type, AttributePlurality, Required, ClassName, Writability)
        {
            Description = Description,

            // caseExact describes how text compares, so only Text attributes carry the declaration (#2051).
            CaseExact = CaseExact && Type == AttributeDataType.Text,

            // RFC 7643 dateTime may carry a fraction, but a service provider need not keep one and most hold whole
            // seconds, so export sends and compares at the second unless an administrator knows better (#2153, D2).
            DateTimeResolution = Type == AttributeDataType.DateTime ? DateTimeResolution.Second : null
        };
    }
}
