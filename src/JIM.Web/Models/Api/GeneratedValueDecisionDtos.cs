// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// One generated value held for an administrator's decision, or released from one (Unique Value Generation, #242,
/// release 4): a target rejected it as already in use and JIM did not correct it.
/// </summary>
public class GeneratedValueDecisionDto
{
    /// <summary>The generated value's id: what <c>allow-rename</c> and <c>try-again</c> take.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// <c>NeedsDecision</c> (the export is held until someone acts), <c>RenameAllowed</c> (an administrator allowed the
    /// rename; the next export run chooses the next free value), or <c>Released</c> (nothing is waiting; only on a read
    /// by id or an action's response, never in a list).
    /// </summary>
    public GeneratedValueDecisionStatus Status { get; set; }

    /// <summary>
    /// Why the value is held: <c>AnchoredElsewhere</c> (another Connected System has already accepted it, so correcting
    /// it would rename that account; <see cref="AnchoredByConnectedSystemId"/> names it), <c>CannotTell</c> (a Connected
    /// System's connector space was cleared and has not been fully imported since, so JIM cannot tell whether it holds
    /// the value; <see cref="AnchoredByConnectedSystemId"/> names it), <c>RemediationLimitReached</c> (corrected
    /// <see cref="RemediationCount"/> times and still rejected), or <c>NoValueAvailable</c> (no other value could be
    /// generated). Null when not recorded.
    /// </summary>
    public GeneratedValueNeedsDecisionReason? Reason { get; set; }

    /// <summary>The Metaverse Object the value belongs to, or (export mode) the one its account is joined to.</summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>The display name of <see cref="MetaverseObjectId"/>.</summary>
    public string? MetaverseObjectDisplayName { get; set; }

    /// <summary>The name of <see cref="MetaverseObjectId"/>'s Metaverse Object Type.</summary>
    public string? MetaverseObjectTypeName { get; set; }

    /// <summary>Export mode only: the Connected System Object (account) the value belongs to.</summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>Export mode only: the Connected System holding <see cref="ConnectedSystemObjectId"/>.</summary>
    public int? ConnectedSystemObjectConnectedSystemId { get; set; }

    /// <summary>The attribute the value was generated for.</summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>The value the target rejected, which is still the value JIM holds.</summary>
    public string Value { get; set; } = null!;

    /// <summary>How many times Collision Remediation has already corrected this value.</summary>
    public int RemediationCount { get; set; }

    /// <summary>The Connected System that rejected the value.</summary>
    public int? RejectedByConnectedSystemId { get; set; }

    /// <summary>The name of <see cref="RejectedByConnectedSystemId"/>; null when it has since been deleted.</summary>
    public string? RejectedByConnectedSystemName { get; set; }

    /// <summary>The Connected System that anchors the value, or cannot tell whether it does.</summary>
    public int? AnchoredByConnectedSystemId { get; set; }

    /// <summary>The name of <see cref="AnchoredByConnectedSystemId"/>; null when it has since been deleted.</summary>
    public string? AnchoredByConnectedSystemName { get; set; }

    /// <summary>When the value began waiting on a decision (UTC).</summary>
    public DateTime? Since { get; set; }

    /// <summary>When the rename was allowed (UTC); only while <see cref="Status"/> is <c>RenameAllowed</c>.</summary>
    public DateTime? RenameAllowedAt { get; set; }

    /// <summary>Who allowed the rename; only while <see cref="Status"/> is <c>RenameAllowed</c>.</summary>
    public string? RenameAllowedBy { get; set; }

    /// <summary>The Synchronisation Rule whose Attribute Flow generated the value.</summary>
    public int SyncRuleId { get; set; }

    /// <summary>The name of <see cref="SyncRuleId"/>.</summary>
    public string? SyncRuleName { get; set; }

    /// <summary>The Attribute Flow (mapping) that generated the value.</summary>
    public int SyncRuleMappingId { get; set; }

    public static GeneratedValueDecisionDto FromHeader(GeneratedValueDecisionHeader header) => new()
    {
        Id = header.AssignmentId,
        Status = header.Status,
        Reason = header.Reason,
        MetaverseObjectId = header.MetaverseObjectId,
        MetaverseObjectDisplayName = header.MetaverseObjectDisplayName,
        MetaverseObjectTypeName = header.MetaverseObjectTypeName,
        ConnectedSystemObjectId = header.ConnectedSystemObjectId,
        ConnectedSystemObjectConnectedSystemId = header.ConnectedSystemObjectConnectedSystemId,
        AttributeName = header.AttributeName,
        Value = header.Value,
        RemediationCount = header.RemediationCount,
        RejectedByConnectedSystemId = header.RejectedByConnectedSystemId,
        RejectedByConnectedSystemName = header.RejectedByConnectedSystemName,
        AnchoredByConnectedSystemId = header.AnchoredByConnectedSystemId,
        AnchoredByConnectedSystemName = header.AnchoredByConnectedSystemName,
        Since = header.Since,
        RenameAllowedAt = header.RenameAllowedAt,
        RenameAllowedBy = header.RenameAllowedBy,
        SyncRuleId = header.SyncRuleId,
        SyncRuleName = header.SyncRuleName,
        SyncRuleMappingId = header.SyncRuleMappingId
    };
}

/// <summary>
/// Which held generated values a bulk "Try again" applies to. The criteria combine: "these ids, if they still need a
/// decision" is expressible, so a caller acting on what a list showed a moment ago never acts on a value that has moved on.
/// Only values needing a decision are released; an allowed rename is left as it is.
/// </summary>
public class GeneratedValueDecisionActionRequest : IValidatableObject
{
    /// <summary>
    /// The largest number of values that may be named in one request; a caller with more wants a filter instead.
    /// </summary>
    public const int MaximumIds = 1000;

    /// <summary>
    /// Restrict to values involving one Connected System: it rejected the value, it anchors it, or the value is exported
    /// to and checked in it.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>Restrict to values generated by Attribute Flows on one Synchronisation Rule.</summary>
    public int? SyncRuleId { get; set; }

    /// <summary>Restrict to one Metaverse Object's values (and those on accounts joined to it).</summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>Restrict to these values, by id. Combines with the other criteria.</summary>
    public IReadOnlyCollection<Guid>? Ids { get; set; }

    /// <summary>
    /// Confirms that a request naming no criteria is meant to release every value needing a decision. Required so an
    /// empty body never acts on everything by accident.
    /// </summary>
    public bool ApplyToAllDecisions { get; set; }

    /// <summary>Translates the request into the filter the application layer takes.</summary>
    public GeneratedValueDecisionFilter ToFilter() => new()
    {
        ConnectedSystemId = ConnectedSystemId,
        SyncRuleId = SyncRuleId,
        MetaverseObjectId = MetaverseObjectId,
        Ids = Ids
    };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Ids is { Count: > MaximumIds })
        {
            yield return new ValidationResult(
                $"No more than {MaximumIds} generated values may be named in one request; narrow the request with a filter instead.",
                [nameof(Ids)]);
        }

        if (!ToFilter().HasCriteria && !ApplyToAllDecisions)
        {
            yield return new ValidationResult(
                "This request names no generated values. Supply at least one criterion, or set applyToAllDecisions to release every value needing a decision.",
                [nameof(ApplyToAllDecisions)]);
        }
    }
}

/// <summary>
/// What a bulk "Try again" did.
/// </summary>
public class GeneratedValueDecisionActionResponse
{
    /// <summary>
    /// How many values were released for the next export run to try again. Zero is a valid outcome: a value already
    /// released, or whose rename has been allowed, no longer matches.
    /// </summary>
    public int AffectedCount { get; set; }
}
