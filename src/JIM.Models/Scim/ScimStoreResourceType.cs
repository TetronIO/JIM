// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Scim;

/// <summary>
/// One kind of resource a Connected System's SCIM store accepts, as data rather than code: the RFC 7643 section 6
/// resource type document JIM publishes at <c>/ResourceTypes</c>, held per Connected System.
/// </summary>
/// <remarks>
/// <para>
/// User and Group are seeded for every Connected System using the SCIM 2.0 Service Provider Connector; a custom
/// resource type is another row, and needs no change to the store's shape. The store never interprets
/// <see cref="Definition"/>: the SCIM server parses it, together with the <see cref="ScimStoreSchema"/> documents it
/// names, into the schema that drives intake, PATCH, projection, uniqueness and references.
/// </para>
/// <para>
/// <see cref="Name"/> and <see cref="Endpoint"/> repeat the definition's <c>name</c> and <c>endpoint</c> so requests can
/// be routed by an indexed column; they are always written together with it.
/// </para>
/// </remarks>
public class ScimStoreResourceType
{
    public int Id { get; set; }

    /// <summary>
    /// The Connected System whose store accepts this resource type.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The resource type's name, for example <c>User</c>; unique within the Connected System.
    /// </summary>
    [MaxLength(ScimStoreLimits.NameMaxLength)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// The endpoint the resource type is served at, relative to the Connected System's base URL, for example
    /// <c>/Users</c>; unique within the Connected System.
    /// </summary>
    [MaxLength(ScimStoreLimits.NameMaxLength)]
    public string Endpoint { get; set; } = null!;

    /// <summary>
    /// The RFC 7643 resource type document (JSON): name, endpoint, base schema and schema extensions.
    /// </summary>
    public string Definition { get; set; } = null!;
}
