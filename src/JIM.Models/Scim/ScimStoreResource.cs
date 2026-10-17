// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Scim;

/// <summary>
/// One User or Group an identity provider has pushed to a Connected System using the SCIM 2.0 Service Provider
/// Connector, as the SCIM store holds it: the sanitised resource, plus the columns the store indexes, versions and
/// imports by.
/// </summary>
/// <remarks>
/// The store is written only by the SCIM endpoints, through one write session at a time per Connected System, and
/// read by those endpoints and by the Service Provider Connector's imports. Nothing else writes it; in particular a
/// synchronisation run never does, so <see cref="Version"/> changes only when the identity provider changes the
/// resource. A deleted resource stays as a tombstone (<see cref="DeletedAt"/> set, everything identifying cleared)
/// until a Delta Import has read it and the purge removes it.
/// </remarks>
public class ScimStoreResource
{
    /// <summary>
    /// The SCIM <c>id</c>: assigned by JIM when the resource is created, and never changed. It is also the external id
    /// of the Connected System Object the import creates.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The Connected System whose store this resource belongs to.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    public ScimStoreResourceType ResourceType { get; set; }

    /// <summary>
    /// The User's <c>userName</c>, unique among the Connected System's live Users without regard to case. Null for a
    /// Group and for a tombstone.
    /// </summary>
    [MaxLength(ScimStoreLimits.IdentifierMaxLength)]
    public string? UserName { get; set; }

    /// <summary>
    /// The identity provider's own identifier for the resource, as sent. Unique among the Connected System's live
    /// resources of the same type. Null when not sent, and for a tombstone.
    /// </summary>
    [MaxLength(ScimStoreLimits.IdentifierMaxLength)]
    public string? ExternalId { get; set; }

    /// <summary>
    /// The resource's <c>displayName</c>, held as a column so it can be filtered on and shown beside a Group's members
    /// without reading every member's payload. Null when not sent, and for a tombstone.
    /// </summary>
    [MaxLength(ScimStoreLimits.IdentifierMaxLength)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Starts at 1 and increases by one with every write to the resource, including a membership change caused by a
    /// member's deletion. Returned as the weak entity tag <c>W/"n"</c>.
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    /// The Connected System's change sequence at the write that last changed this resource. Imports read the store
    /// by this, so it is assigned in commit order (see <see cref="ScimStoreState"/>).
    /// </summary>
    public long ChangeSequence { get; set; }

    /// <summary>
    /// When the resource was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// When the resource last changed (UTC); <c>meta.lastModified</c>.
    /// </summary>
    public DateTime LastModified { get; set; }

    /// <summary>
    /// When the resource was deleted (UTC), making this row a tombstone; null while it is live.
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// The resource's attributes as JSON, sanitised: no write-only attribute, no <c>id</c>, <c>meta</c> or
    /// <c>schemas</c>, and no Group <c>members</c> (those are rows of <see cref="ScimStoreGroupMember"/>). Null for a
    /// tombstone.
    /// </summary>
    public string? Payload { get; set; }
}
