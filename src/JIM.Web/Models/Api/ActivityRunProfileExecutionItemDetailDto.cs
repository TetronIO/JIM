// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;

namespace JIM.Web.Models.Api;

/// <summary>
/// One Run Profile Execution Item: what happened to one object in a Run Profile execution, and, when it failed,
/// why (#2032). The list endpoint returns headers, which carry the error type but not the message; this carries the
/// message and stack trace, the snapshots taken when the item was recorded, and the ids of what it is about.
/// </summary>
public class ActivityRunProfileExecutionItemDetailDto
{
    /// <summary>The item's unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The Run Profile Activity the item belongs to.</summary>
    public Guid ActivityId { get; set; }

    /// <summary>What happened to the object.</summary>
    /// <remarks>Serialised as the enum member name.</remarks>
    public ObjectChangeType ObjectChangeType { get; set; }

    /// <summary>Why nothing changed, when nothing did.</summary>
    /// <remarks>Serialised as the enum member name.</remarks>
    public NoChangeReason? NoChangeReason { get; set; }

    /// <summary>The Connected System Object the item is about, when it still exists.</summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>The Metaverse Object the item is about, where it records one (kept as history; it may since have been deleted).</summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>The Pending Export the item is about, for an export.</summary>
    public Guid? PendingExportId { get; set; }

    /// <summary>The object's external ID when the item was recorded.</summary>
    public string? ExternalIdSnapshot { get; set; }

    /// <summary>The object's display name when the item was recorded.</summary>
    public string? DisplayNameSnapshot { get; set; }

    /// <summary>The object's type name when the item was recorded.</summary>
    public string? ObjectTypeSnapshot { get; set; }

    /// <summary>The kind of failure; <c>NotSet</c> or null when the object did not fail.</summary>
    /// <remarks>Serialised as the enum member name.</remarks>
    public ActivityRunProfileExecutionItemErrorType? ErrorType { get; set; }

    /// <summary>What went wrong, in words; null when the object did not fail.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>The stack trace, when the failure came from an exception.</summary>
    public string? ErrorStackTrace { get; set; }

    /// <summary>How many Metaverse attributes changed alongside a join, projection or disconnection.</summary>
    public int? AttributeFlowCount { get; set; }

    /// <summary>
    /// How many of the object's reference values the import could not resolve to a Connected System Object, under
    /// any Unresolved Reference Handling and whatever error the item carries; null when none were left unresolved.
    /// The values themselves are on the Connected System Object.
    /// </summary>
    public int? UnresolvedReferenceCount { get; set; }

    /// <summary>
    /// Every value the import found on the object but could not import, in the order they were reported: values the
    /// Connector could not read as their attribute's type, and values JIM could not hold as read. JIM left each
    /// attribute as it already held it. Each value's Source says who found it: Connector when the Connector could not
    /// read it, Jim when JIM could not hold it as read (the portal's Found By). Empty when there were none.
    /// </summary>
    public List<AttributeValueErrorDto> AttributeValueErrors { get; set; } = [];

    /// <summary>
    /// How many values could not be imported, which can exceed <see cref="AttributeValueErrors"/> where the item keeps only
    /// the first thousand; null when there were none.
    /// </summary>
    public int? AttributeValueErrorTotal { get; set; }

    /// <summary>Where in the Connected System the object was read from, such as a file and row, where the Connector says.</summary>
    public string? SourceLocation { get; set; }

    /// <summary>Outcome types with counts, for example <c>Projected:1,AttributeFlow:12</c>.</summary>
    public string? OutcomeSummary { get; set; }

    /// <summary>
    /// For an <c>AmbiguousMatch</c>, the Metaverse Objects the match found, and for an <c>AmbiguousProvisioningMatch</c>,
    /// the Connected System Objects (#2052): up to 10, ordered by display name, each with whether it still exists and,
    /// for a Metaverse Object, what is joined to it now. Empty for any other item.
    /// </summary>
    public List<MatchCandidateDetailDto> MatchCandidates { get; set; } = [];

    /// <summary>
    /// For an object joined to an existing Metaverse Object by matching, the id of the Object Matching Rule that found
    /// it; null for any other item. The rule may since have been changed or deleted; the description below records
    /// what it compared at the time.
    /// </summary>
    public int? JoinObjectMatchingRuleId { get; set; }

    /// <summary>
    /// For an object joined by matching, what the Object Matching Rule compared when the join was made, for example
    /// <c>Employee ID equals employeeId</c>; null for any other item.
    /// </summary>
    public string? JoinObjectMatchingRuleDescriptionSnapshot { get; set; }

    /// <summary>
    /// For an object joined by matching, the value the join matched on: what the rule computed from the object, with
    /// several separated by commas for a multi-valued source. Null for any other item.
    /// </summary>
    public string? JoinMatchedValue { get; set; }

    /// <summary>
    /// What the run did to the object, as a tree: the root outcomes in order, each with the outcomes that followed from
    /// it nested under <c>children</c> (for example Projected, then Attribute Flow and Provisioned, then the Pending
    /// Export it staged). Null unless asked for (<c>include=outcomes</c>, or <c>-IncludeOutcomes</c> in PowerShell); an
    /// empty list when asked for and the item recorded none.
    /// </summary>
    public List<SyncOutcomeNodeDto>? Outcomes { get; set; }

    /// <summary>
    /// The attribute changes recorded with the item, in the shapes object change history uses: the Metaverse Object
    /// change a synchronisation made, the Connected System Object change the item recorded, and what each
    /// Pending Export a synchronisation staged would write. Null unless asked for (<c>include=changes</c>, or
    /// <c>-IncludeChanges</c> in PowerShell).
    /// </summary>
    public ExecutionItemChanges? Changes { get; set; }

    /// <summary>
    /// Creates the detail from an item's own row; no navigation property is read, so the light retrieval suffices.
    /// </summary>
    public static ActivityRunProfileExecutionItemDetailDto FromEntity(ActivityRunProfileExecutionItem entity)
    {
        var attributeValueErrors = ImportedAttributeValueErrors.FromJson(entity.AttributeValueErrorsJson);

        return new ActivityRunProfileExecutionItemDetailDto
        {
            Id = entity.Id,
            ActivityId = entity.ActivityId,
            ObjectChangeType = entity.ObjectChangeType,
            NoChangeReason = entity.NoChangeReason,
            ConnectedSystemObjectId = entity.ConnectedSystemObjectId,
            MetaverseObjectId = entity.MetaverseObjectId,
            PendingExportId = entity.PendingExportId,
            ExternalIdSnapshot = entity.ExternalIdSnapshot,
            DisplayNameSnapshot = entity.DisplayNameSnapshot,
            ObjectTypeSnapshot = entity.ObjectTypeSnapshot,
            ErrorType = entity.ErrorType,
            ErrorMessage = entity.ErrorMessage,
            ErrorStackTrace = entity.ErrorStackTrace,
            AttributeFlowCount = entity.AttributeFlowCount,
            UnresolvedReferenceCount = entity.UnresolvedReferenceCount,
            AttributeValueErrors = attributeValueErrors?.Values.Select(AttributeValueErrorDto.FromModel).ToList() ?? [],
            AttributeValueErrorTotal = attributeValueErrors?.Total,
            SourceLocation = entity.SourceLocation,
            OutcomeSummary = entity.OutcomeSummary
        };
    }
}
