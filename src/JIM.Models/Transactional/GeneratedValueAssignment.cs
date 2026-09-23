// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// JIM's sticky ownership of one object's generated attribute value (Unique Value Generation, #242, plan
/// decision 17). This is state, not history: it exists exactly while a generated mapping is responsible for the
/// object's attribute, and is deleted (by cascade) the moment that stops being true, whether because the object
/// or the generated mapping is deleted, or because page-flush reconciliation finds another contributor has won
/// the attribute. History lives in Activities and causality; a deleted assignment's value is remembered, when
/// "never reuse" applies, by the retired values register (release 2).
/// <para>
/// Exactly one of two modes is populated, matching whether the flow that produced it is an import or export
/// mapping: <see cref="MetaverseObjectId"/>/<see cref="MetaverseAttributeId"/> for import mode, or
/// <see cref="ConnectedSystemObjectId"/>/<see cref="ConnectedSystemObjectTypeAttributeId"/> for export mode.
/// </para>
/// </summary>
public class GeneratedValueAssignment
{
    public Guid Id { get; set; }

    /// <summary>
    /// Import mode: the Metaverse Object the value belongs to.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    public MetaverseObject? MetaverseObject { get; set; }

    /// <summary>
    /// Import mode: the Metaverse attribute the value was generated for.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    public MetaverseAttribute? MetaverseAttribute { get; set; }

    /// <summary>
    /// Export mode: the Connected System Object the value belongs to.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    public ConnectedSystemObject? ConnectedSystemObject { get; set; }

    /// <summary>
    /// Export mode: the Connected System Object Type attribute the value was generated for.
    /// </summary>
    public int? ConnectedSystemObjectTypeAttributeId { get; set; }

    public ConnectedSystemObjectTypeAttribute? ConnectedSystemObjectTypeAttribute { get; set; }

    /// <summary>
    /// The generated value, as held on the object.
    /// </summary>
    public string Value { get; set; } = null!;

    /// <summary>
    /// The lower-cased form of <see cref="Value"/> that uniqueness is enforced on (plan decision 13: text
    /// uniqueness is case-insensitive throughout Unique Value Generation). Unique, together with the owning
    /// attribute, across every live assignment.
    /// </summary>
    public string NormalisedValue { get; set; } = null!;

    /// <summary>
    /// The value this assignment held before its most recent Collision Remediation revision (release 4). Null
    /// until the first remediation.
    /// </summary>
    public string? PreviousValue { get; set; }

    public GeneratedValueAssignmentState State { get; set; }

    /// <summary>
    /// The generated mapping that produced this assignment. Cascade delete: removing the mapping's generation
    /// settings (by recall, keep, or the mapping's source type changing away from generated) removes every
    /// assignment it produced.
    /// </summary>
    public int SyncRuleMappingGenerationId { get; set; }

    public SyncRuleMappingGeneration? SyncRuleMappingGeneration { get; set; }

    /// <summary>
    /// True when the value was adopted from an existing accepted value rather than generated (plan decision 5:
    /// "adopt before generate"). An adopted assignment is created directly in <see cref="GeneratedValueAssignmentState.Committed"/>.
    /// </summary>
    public bool Adopted { get; set; }

    /// <summary>
    /// How many times Collision Remediation has revised this assignment's value (release 4). The attempt-limit
    /// scope for remediation is per assignment lifetime, distinct from <see cref="SyncRuleMappingGeneration.AttemptLimit"/>,
    /// which bounds a single generation attempt (plan decision 15).
    /// </summary>
    public int RemediationCount { get; set; }

    /// <summary>
    /// Whether an administrator has authorised JIM to rename every system holding this value on its next
    /// rejection (the "allow the rename" exit from NeedsDecision, release 4). Records the authorisation only;
    /// the worker performs the rename at the next rejection, since the portal cannot probe live connector state.
    /// </summary>
    public bool RenameAuthorised { get; set; }

    public DateTime? RenameAuthorisedAt { get; set; }

    /// <summary>
    /// The display name of the administrator who authorised the rename, retained even if the principal is later
    /// deleted.
    /// </summary>
    public string? RenameAuthorisedByName { get; set; }

    /// <summary>
    /// The Connected System whose export rejected this value, naming it for the NeedsDecision error. Plain int,
    /// deliberately not a foreign key: the id must still name the system in the error message after the system
    /// itself has been deleted, which a foreign key would prevent (either by blocking the system's deletion or by
    /// being nulled out and losing the fact being reported).
    /// </summary>
    public int? RejectedByConnectedSystemId { get; set; }

    /// <summary>
    /// The Connected System where this value is already held by another object (anchoring it against automatic
    /// Collision Remediation). Plain int for the same reason as <see cref="RejectedByConnectedSystemId"/>.
    /// </summary>
    public int? AnchoredByConnectedSystemId { get; set; }

    /// <summary>
    /// The Run Profile Execution Item whose Collision Remediation last revised this assignment's value. Plain
    /// Guid, not a foreign key, for the same reason as the two Connected System ids above: execution items are
    /// bulk-managed and this is an advisory pointer, not a relationship the schema should have to protect.
    /// </summary>
    public Guid? RemediatedByActivityRunProfileExecutionItemId { get; set; }

    /// <summary>
    /// When this assignment entered <see cref="GeneratedValueAssignmentState.NeedsDecision"/> (UTC). Null outside
    /// that state.
    /// </summary>
    public DateTime? NeedsDecisionEnteredAt { get; set; }

    /// <summary>
    /// The Run Profile Execution Item whose rejection put this assignment into NeedsDecision. Plain Guid, not a
    /// foreign key, for the same reason as <see cref="RemediatedByActivityRunProfileExecutionItemId"/>.
    /// </summary>
    public Guid? NeedsDecisionActivityRunProfileExecutionItemId { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this assignment first became <see cref="GeneratedValueAssignmentState.Committed"/>. Null while still
    /// <see cref="GeneratedValueAssignmentState.Proposed"/>.
    /// </summary>
    public DateTime? CommittedAt { get; set; }
}
