// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Scim;

/// <summary>
/// One schema a Connected System's SCIM store uses, as data rather than code: the RFC 7643 section 7 schema document
/// JIM publishes at <c>/Schemas</c>, attribute characteristics included, held per Connected System. A resource type's
/// base schema and its extensions are each one of these.
/// </summary>
/// <remarks>
/// The core User, Group and Enterprise User schemas are seeded for every Connected System using the SCIM 2.0 Service
/// Provider Connector; a custom extension or a custom resource type's schema is another row. <see cref="Urn"/> repeats
/// the definition's <c>id</c> for lookup and is always written together with it.
/// </remarks>
public class ScimStoreSchema
{
    public int Id { get; set; }

    /// <summary>
    /// The Connected System whose store uses this schema.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The schema's URN, for example <c>urn:ietf:params:scim:schemas:core:2.0:User</c>; unique within the Connected
    /// System.
    /// </summary>
    [MaxLength(ScimStoreLimits.AttributeMaxLength)]
    public string Urn { get; set; } = null!;

    /// <summary>
    /// The RFC 7643 schema document (JSON), with every attribute's characteristics.
    /// </summary>
    public string Definition { get; set; } = null!;
}
