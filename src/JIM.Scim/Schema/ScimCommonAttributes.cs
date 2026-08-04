// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Scim.Schema;

/// <summary>
/// The attributes every SCIM resource carries (RFC 7643 section 3.1): <c>id</c>, <c>externalId</c> and
/// the <c>meta</c> sub-attributes.
/// <para>
/// These are defined by the specification rather than by any schema document, so they never appear in a
/// provider's <c>/Schemas</c> response and have to be added deliberately. Without them a Connected
/// System would have no external identifier to anchor its objects on, and no last-modified attribute
/// for delta import to watermark against.
/// </para>
/// </summary>
public static class ScimCommonAttributes
{
    /// <summary>The provider-assigned, immutable identifier JIM anchors Connected System Objects on.</summary>
    public const string Id = "id";

    /// <summary>The client-assigned identifier, writable so JIM can stamp its own reference on export.</summary>
    public const string ExternalId = "externalId";

    /// <summary>The complex attribute the resource metadata sub-attributes live under.</summary>
    private const string MetaAttribute = "meta";

    /// <summary>The attribute the last-modified delta strategy watermarks against.</summary>
    public const string MetaLastModified = "meta.lastModified";

    /// <summary>The attribute holding the resource's entity tag, used by conditional change detection.</summary>
    public const string MetaVersion = "meta.version";

    /// <summary>
    /// Builds the common attributes for a resource type.
    /// </summary>
    /// <param name="schemaUrn">The resource type's base schema URN, recorded as the class name.</param>
    public static List<ScimFlattenedAttribute> For(string schemaUrn)
    {
        return
        [
            // Required and read-only: the provider assigns it, and JIM must never try to write it.
            new ScimFlattenedAttribute(Id, Id, AttributeDataType.Text, AttributePlurality.SingleValued,
                required: true, AttributeWritability.ReadOnly, schemaUrn,
                "The service provider's unique, immutable identifier for the resource."),

            new ScimFlattenedAttribute(ExternalId, ExternalId, AttributeDataType.Text, AttributePlurality.SingleValued,
                required: false, AttributeWritability.Writable, schemaUrn,
                "An identifier assigned by the provisioning client, which JIM can populate on export."),

            Meta("meta.resourceType", AttributeDataType.Text, "The name of the resource type, for example User."),
            Meta("meta.created", AttributeDataType.DateTime, "When the resource was created."),
            Meta(MetaLastModified, AttributeDataType.DateTime, "When the resource was last changed. Delta import watermarks against this."),
            Meta("meta.location", AttributeDataType.Text, "The URI of the resource."),
            Meta(MetaVersion, AttributeDataType.Text, "The resource's entity tag, used for conditional change detection.")
        ];

        ScimFlattenedAttribute Meta(string name, AttributeDataType type, string description)
        {
            // Every meta sub-attribute is provider-maintained, so none is a valid export target.
            return new ScimFlattenedAttribute(name, name, type, AttributePlurality.SingleValued,
                required: false, AttributeWritability.ReadOnly, schemaUrn, description,
                ScimValueAccess.ComplexSubAttribute, MetaAttribute, name["meta.".Length..]);
        }
    }
}
