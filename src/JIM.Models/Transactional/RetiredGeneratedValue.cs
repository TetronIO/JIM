// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// One entry in the retired values register (Unique Value Generation, #242, plan decision 4; PRD FR 28): a value a
/// generated Attribute Flow issued, whose assignment has since been deleted while "Never reuse a value" was on (or
/// the token was a Sequence, which never reuses). Every availability gate treats a retired value as taken, so it is
/// never issued again for the attribute, whichever flow generates it.
/// <para>
/// The register is kept per attribute, not per flow: exactly one of <see cref="MetaverseAttributeId"/> and
/// <see cref="ConnectedSystemObjectTypeAttributeId"/> is populated, and the value is unique for that attribute
/// case-insensitively (a <c>LOWER("NormalisedValue")</c> unique index), so retiring the same value twice is a
/// no-op. It is never pruned: "Start again" on a Sequence flow is the only thing that clears it, and it goes with
/// the attribute when the attribute is deleted.
/// </para>
/// </summary>
public class RetiredGeneratedValue
{
    public long Id { get; set; }

    /// <summary>
    /// The Metaverse attribute the value was generated for, for an import flow. Exactly one of this and
    /// <see cref="ConnectedSystemObjectTypeAttributeId"/> is set.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    public MetaverseAttribute? MetaverseAttribute { get; set; }

    /// <summary>
    /// The Connected System Object Type attribute the value was generated for, for an export flow. Exactly one of
    /// this and <see cref="MetaverseAttributeId"/> is set.
    /// </summary>
    public int? ConnectedSystemObjectTypeAttributeId { get; set; }

    public ConnectedSystemObjectTypeAttribute? ConnectedSystemObjectTypeAttribute { get; set; }

    /// <summary>
    /// The value as it was issued, for display.
    /// </summary>
    public string Value { get; set; } = null!;

    /// <summary>
    /// The value lower-cased: what the gates compare against, because uniqueness is case-insensitive (FR 31).
    /// </summary>
    public string NormalisedValue { get; set; } = null!;

    /// <summary>
    /// When the value was retired (UTC).
    /// </summary>
    public DateTime RetiredAt { get; set; }

    /// <summary>
    /// Why the value was retired.
    /// </summary>
    public RetiredGeneratedValueReason Reason { get; set; }

    /// <summary>
    /// The display name of the object that held the value, captured at retirement, so the register can still say
    /// who held it once the object has been deleted.
    /// </summary>
    public string? FromObjectDisplayName { get; set; }

    /// <summary>
    /// The object that held the value: a Metaverse Object id for an import flow, a Connected System Object id for an
    /// export flow. Deliberately not a foreign key: the object may be deleted, and the register must outlive it.
    /// </summary>
    public Guid? FromObjectId { get; set; }

    /// <summary>
    /// The Activity during which the value was retired (the synchronisation that found another Attribute Flow had
    /// taken the attribute over), so the object's history can say what caused it. Null where none was known: an
    /// object deletion, or a flow removal, whose retirements are written beside the deletion itself. Deliberately
    /// not a foreign key: Activities are pruned by history retention and the register outlives them.
    /// </summary>
    public Guid? ActivityId { get; set; }
}
