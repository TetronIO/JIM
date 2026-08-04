// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Scim.Discovery;

namespace JIM.Scim.Schema;

/// <summary>
/// The core schemas as RFC 7643 defines them, used when a service provider does not publish
/// <c>/Schemas</c>.
/// <para>
/// A provider that answers on <c>/Users</c> but not <c>/Schemas</c> is common enough to be worth
/// supporting: refusing to configure it would rule out working providers over a missing discovery
/// document. These definitions are the specification's, so an attribute present here is one the
/// provider is required to understand.
/// </para>
/// </summary>
public static class ScimCoreSchemas
{
    /// <summary>
    /// The core User schema (RFC 7643 section 4.1).
    /// </summary>
    public static ScimSchema User()
    {
        return new ScimSchema
        {
            Id = ScimUrns.User,
            Name = "User",
            Description = "User Account",
            Attributes =
            [
                Simple("userName", ScimAttributeTypes.String, required: true, description: "Unique identifier for the User, typically used to log in."),
                Complex("name", multiValued: false, subAttributes:
                [
                    Simple("formatted", ScimAttributeTypes.String),
                    Simple("familyName", ScimAttributeTypes.String),
                    Simple("givenName", ScimAttributeTypes.String),
                    Simple("middleName", ScimAttributeTypes.String),
                    Simple("honorificPrefix", ScimAttributeTypes.String),
                    Simple("honorificSuffix", ScimAttributeTypes.String)
                ]),
                Simple("displayName", ScimAttributeTypes.String),
                Simple("nickName", ScimAttributeTypes.String),
                Simple("profileUrl", ScimAttributeTypes.Reference),
                Simple("title", ScimAttributeTypes.String),
                Simple("userType", ScimAttributeTypes.String),
                Simple("preferredLanguage", ScimAttributeTypes.String),
                Simple("locale", ScimAttributeTypes.String),
                Simple("timezone", ScimAttributeTypes.String),
                Simple("active", ScimAttributeTypes.Boolean, description: "Whether the User's account is enabled."),
                Simple("password", ScimAttributeTypes.String, mutability: ScimMutability.WriteOnly, returned: "never"),
                MultiValuedWithCanonicalTypes("emails", ScimAttributeTypes.String, ["work", "home", "other"]),
                MultiValuedWithCanonicalTypes("phoneNumbers", ScimAttributeTypes.String, ["work", "home", "mobile", "fax", "pager", "other"]),
                MultiValuedWithCanonicalTypes("ims", ScimAttributeTypes.String, ["aim", "gtalk", "icq", "xmpp", "msn", "skype", "qq", "yahoo"]),
                MultiValuedWithCanonicalTypes("photos", ScimAttributeTypes.Reference, ["photo", "thumbnail"]),
                Complex("addresses", multiValued: true, subAttributes:
                [
                    Simple("formatted", ScimAttributeTypes.String),
                    Simple("streetAddress", ScimAttributeTypes.String),
                    Simple("locality", ScimAttributeTypes.String),
                    Simple("region", ScimAttributeTypes.String),
                    Simple("postalCode", ScimAttributeTypes.String),
                    Simple("country", ScimAttributeTypes.String),
                    CanonicalType(["work", "home", "other"])
                ]),
                ReferenceCollection("groups", ["direct", "indirect"], ScimMutability.ReadOnly),
                MultiValuedWithCanonicalTypes("entitlements", ScimAttributeTypes.String, []),
                MultiValuedWithCanonicalTypes("roles", ScimAttributeTypes.String, []),
                MultiValuedWithCanonicalTypes("x509Certificates", ScimAttributeTypes.Binary, [])
            ]
        };
    }

    /// <summary>
    /// The core Group schema (RFC 7643 section 4.2).
    /// </summary>
    public static ScimSchema Group()
    {
        return new ScimSchema
        {
            Id = ScimUrns.Group,
            Name = "Group",
            Description = "Group",
            Attributes =
            [
                Simple("displayName", ScimAttributeTypes.String, required: true, description: "A human-readable name for the Group."),
                ReferenceCollection("members", ["User", "Group"], ScimMutability.ReadWrite)
            ]
        };
    }

    /// <summary>
    /// The Enterprise User extension schema (RFC 7643 section 4.3).
    /// </summary>
    public static ScimSchema EnterpriseUser()
    {
        return new ScimSchema
        {
            Id = ScimUrns.EnterpriseUser,
            Name = "EnterpriseUser",
            Description = "Enterprise User",
            Attributes =
            [
                Simple("employeeNumber", ScimAttributeTypes.String),
                Simple("costCenter", ScimAttributeTypes.String),
                Simple("organization", ScimAttributeTypes.String),
                Simple("division", ScimAttributeTypes.String),
                Simple("department", ScimAttributeTypes.String),
                Complex("manager", multiValued: false, subAttributes:
                [
                    Simple("value", ScimAttributeTypes.String),
                    new ScimSchemaAttribute { Name = "$ref", Type = ScimAttributeTypes.Reference, ReferenceTypes = ["User"] },
                    Simple("displayName", ScimAttributeTypes.String, mutability: ScimMutability.ReadOnly)
                ])
            ]
        };
    }

    /// <summary>
    /// The core resource types, used when a provider does not publish <c>/ResourceTypes</c>. The
    /// endpoints are the ones RFC 7644 section 3.2 defines.
    /// </summary>
    public static List<ScimResourceType> ResourceTypes()
    {
        return
        [
            new ScimResourceType
            {
                Id = "User",
                Name = "User",
                Description = "User Account",
                Endpoint = "/Users",
                Schema = ScimUrns.User,
                SchemaExtensions = [new ScimSchemaExtension { Schema = ScimUrns.EnterpriseUser, Required = false }]
            },
            new ScimResourceType
            {
                Id = "Group",
                Name = "Group",
                Description = "Group",
                Endpoint = "/Groups",
                Schema = ScimUrns.Group
            }
        ];
    }

    /// <summary>
    /// Looks up a core schema by URN, returning null when the URN is not one JIM ships a definition for.
    /// </summary>
    public static ScimSchema? ByUrn(string? urn)
    {
        if (string.IsNullOrWhiteSpace(urn))
            return null;

        if (string.Equals(urn, ScimUrns.User, StringComparison.OrdinalIgnoreCase))
            return User();
        if (string.Equals(urn, ScimUrns.Group, StringComparison.OrdinalIgnoreCase))
            return Group();
        if (string.Equals(urn, ScimUrns.EnterpriseUser, StringComparison.OrdinalIgnoreCase))
            return EnterpriseUser();

        return null;
    }

    private static ScimSchemaAttribute Simple(
        string name,
        string type,
        bool required = false,
        string? mutability = null,
        string? returned = null,
        string? description = null)
    {
        return new ScimSchemaAttribute
        {
            Name = name,
            Type = type,
            Required = required,
            Mutability = mutability ?? ScimMutability.ReadWrite,
            Returned = returned ?? "default",
            Description = description
        };
    }

    private static ScimSchemaAttribute Complex(string name, bool multiValued, List<ScimSchemaAttribute> subAttributes, string? mutability = null)
    {
        return new ScimSchemaAttribute
        {
            Name = name,
            Type = ScimAttributeTypes.Complex,
            MultiValued = multiValued,
            Mutability = mutability ?? ScimMutability.ReadWrite,
            SubAttributes = subAttributes
        };
    }

    /// <summary>
    /// The RFC's multi-valued shape for labelled values: a <c>value</c>, a canonically-typed
    /// <c>type</c>, a <c>display</c> and a <c>primary</c> flag.
    /// </summary>
    private static ScimSchemaAttribute MultiValuedWithCanonicalTypes(string name, string valueType, List<string> canonicalValues)
    {
        return Complex(name, multiValued: true, subAttributes:
        [
            Simple("value", valueType),
            Simple("display", ScimAttributeTypes.String, mutability: ScimMutability.ReadOnly),
            CanonicalType(canonicalValues),
            Simple("primary", ScimAttributeTypes.Boolean)
        ]);
    }

    private static ScimSchemaAttribute CanonicalType(List<string> canonicalValues)
    {
        return new ScimSchemaAttribute
        {
            Name = "type",
            Type = ScimAttributeTypes.String,
            Mutability = ScimMutability.ReadWrite,
            CanonicalValues = canonicalValues
        };
    }

    private static ScimSchemaAttribute ReferenceCollection(string name, List<string> referenceTypes, string mutability)
    {
        return Complex(name, multiValued: true, mutability: mutability, subAttributes:
        [
            Simple("value", ScimAttributeTypes.String, mutability: mutability),
            new ScimSchemaAttribute { Name = "$ref", Type = ScimAttributeTypes.Reference, ReferenceTypes = referenceTypes, Mutability = mutability },
            Simple("display", ScimAttributeTypes.String, mutability: ScimMutability.ReadOnly),
            CanonicalType(referenceTypes)
        ]);
    }
}
