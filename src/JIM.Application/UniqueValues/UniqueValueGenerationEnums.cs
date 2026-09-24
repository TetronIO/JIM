// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.UniqueValues;

/// <summary>
/// Which side of a Synchronisation Rule a <see cref="GenerationRequest"/> was raised for (Unique Value
/// Generation, #242). The unique value service is caller-agnostic (FR 21) and knows nothing about
/// synchronisation runs or Synchronisation Rules themselves; this enum is the one place it distinguishes
/// which pair of object/attribute ids on the request is populated, and so which repository gate applies.
/// </summary>
public enum GeneratedValueMode
{
    /// <summary>
    /// An import mapping generating a value for a Metaverse attribute. <see cref="GenerationRequest.MetaverseObjectId"/>
    /// and <see cref="GenerationRequest.MetaverseAttributeId"/> apply.
    /// </summary>
    Import = 0,

    /// <summary>
    /// An export mapping generating a value for a Connected System attribute.
    /// <see cref="GenerationRequest.ConnectedSystemObjectId"/> and
    /// <see cref="GenerationRequest.ConnectedSystemObjectTypeAttributeId"/> apply.
    /// </summary>
    Export = 1
}

/// <summary>
/// What <see cref="UniqueValueGenerationServer.ResolveAsync"/> decided for one <see cref="GenerationRequest"/>
/// (Unique Value Generation, #242). Every member other than <see cref="Waiting"/> corresponds to one of the
/// resolution steps in the plan's "Behaviour (ResolveAsync)" section; the outcome kind is what a caller
/// switches on to decide how to apply the result.
/// </summary>
public enum GenerationOutcomeKind
{
    /// <summary>
    /// A new value was chosen. <see cref="GenerationOutcome.Assignment"/> is a new, unsaved assignment in state
    /// <c>Proposed</c>.
    /// </summary>
    Generated,

    /// <summary>
    /// <see cref="GenerationRequest.AdoptableValue"/> was taken as the assignment (FR 30): a participating
    /// target already held the value, so JIM adopted it rather than generating a new one.
    /// <see cref="GenerationOutcome.Assignment"/> is a new, unsaved assignment in state <c>Committed</c>, with
    /// <see cref="Transactional.GeneratedValueAssignment.Adopted"/> true.
    /// </summary>
    Adopted,

    /// <summary>
    /// A live assignment already exists for the object and attribute; its value is returned unchanged (FR 10).
    /// <see cref="GenerationOutcome.Assignment"/> is that existing, already-persisted assignment.
    /// </summary>
    Sticky,

    /// <summary>
    /// No free candidate was found within <see cref="Logic.SyncRuleMappingGeneration.AttemptLimit"/> (FR 11).
    /// Nothing is written.
    /// </summary>
    Exhausted,

    /// <summary>
    /// A sequence number outgrew <see cref="Logic.SyncRuleMappingGeneration.FixedWidth"/> with
    /// <see cref="Logic.SyncRuleMappingGeneration.OnWidthExceeded"/> set to
    /// <see cref="Logic.GeneratedValueWidthOverflowBehaviour.StopAndReport"/> (FR 25).
    /// </summary>
    WidthExceeded,

    /// <summary>
    /// The token needs a base value (<see cref="Logic.GeneratedValueTokenKind.OnlyIfTaken"/>) and the request
    /// carried none.
    /// </summary>
    NoBaseValue,

    /// <summary>
    /// <see cref="GenerationRequest.AdoptableValue"/> is already claimed by another object: a live assignment,
    /// or this run's reservations.
    /// </summary>
    AdoptionConflict,

    /// <summary>
    /// <see cref="GenerationRequest.StickyOnly"/> was set and no live assignment exists for the object and
    /// attribute: the mapping's base expression could not be evaluated for this object (a required input is
    /// missing and the mapping's Missing Input Behaviour is "contribute no value"), so nothing is generated and
    /// nothing is adopted; the caller waits for a later run where the inputs are available. Appended last so
    /// existing ordinal usages are undisturbed.
    /// </summary>
    Waiting
}

/// <summary>
/// Which value space a candidate is checked and reserved against in a <see cref="UniqueValueReservationSet"/>
/// (Unique Value Generation, #242): a generated Metaverse attribute value and a generated Connected System
/// attribute value are never the same reservation, even where their attribute ids happened to collide, because
/// they are different tables with different uniqueness gates.
/// </summary>
public enum UniqueValueScope
{
    /// <summary>
    /// A candidate for a Metaverse attribute (import mode).
    /// </summary>
    MetaverseAttribute = 0,

    /// <summary>
    /// A candidate for a Connected System Object Type attribute (export mode).
    /// </summary>
    ConnectedSystemAttribute = 1
}
