// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// Why a Metaverse Object is connected where it is, and, on request, why it is not connected elsewhere (#348). Every
/// explanation in it was evaluated at <see cref="EvaluatedAt"/>, against current values.
/// </summary>
public class MetaverseObjectConnectionExplanationsDto
{
    public Guid MetaverseObjectId { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>When every scoping evaluation in the response was made, in UTC.</summary>
    public DateTime EvaluatedAt { get; set; }

    /// <summary>One entry per Connected System Object joined to the Metaverse Object.</summary>
    public List<MetaverseObjectConnectionExplanationDto> Connections { get; set; } = [];

    /// <summary>
    /// One entry per enabled export Synchronisation Rule whose Connected System holds no object joined to this one,
    /// ordered by Connected System then rule; null unless <c>includeNotConnected</c> was requested, and an empty list
    /// when the object is connected everywhere it could be.
    /// </summary>
    public List<NotConnectedEntryDto>? NotConnected { get; set; }

    public static MetaverseObjectConnectionExplanationsDto FromModel(MetaverseObjectConnectionExplanations model) => new()
    {
        MetaverseObjectId = model.MetaverseObjectId,
        DisplayName = model.DisplayName,
        EvaluatedAt = model.EvaluatedAt,
        Connections = model.Connections.Select(MetaverseObjectConnectionExplanationDto.FromModel).ToList(),
        NotConnected = model.NotConnected?.Select(NotConnectedEntryDto.FromModel).ToList()
    };
}

/// <summary>
/// One joined connection and why it exists: the Connections tab's row, how it was joined, the scoping of every relevant
/// enabled Synchronisation Rule evaluated now, and any export rule that cannot connect because this connection holds
/// the Metaverse Object's slot in the Connected System.
/// </summary>
public class MetaverseObjectConnectionExplanationDto
{
    public Guid ConnectedSystemObjectId { get; set; }

    /// <summary>The object's external id, or its id where the external id cannot be shown.</summary>
    public string? DisplayName { get; set; }

    public int ConnectedSystemId { get; set; }

    public string ConnectedSystemName { get; set; } = string.Empty;

    public string ObjectTypeName { get; set; } = string.Empty;

    public ConnectedSystemObjectJoinType JoinType { get; set; }

    /// <summary>True when an enabled import Synchronisation Rule reads this object type: values can flow in from it.</summary>
    public bool IsSource { get; set; }

    /// <summary>True when an enabled export Synchronisation Rule writes this object type: values can flow out to it.</summary>
    public bool IsTarget { get; set; }

    public ConnectedSystemObjectConnectionState State { get; set; }

    /// <summary>Attribute changes on the object's Pending Export during an update; null otherwise.</summary>
    public int? PendingAttributeChangeCount { get; set; }

    public DateTime? LastSynchronised { get; set; }

    /// <summary>How the object came to be joined.</summary>
    public JoinRecordDto Join { get; set; } = new();

    /// <summary>
    /// One explanation per relevant enabled rule, in rule name order: import rules against the object's values, export
    /// rules against the Metaverse Object's. Evaluated now; not the reason at the time of joining.
    /// </summary>
    public List<ScopingExplanationDto> Scoping { get; set; } = [];

    public List<ConnectionObjectTypeConflictDto> Conflicts { get; set; } = [];

    public static MetaverseObjectConnectionExplanationDto FromModel(MetaverseObjectConnectionExplanation model) => new()
    {
        ConnectedSystemObjectId = model.ConnectedSystemObjectId,
        DisplayName = model.DisplayName,
        ConnectedSystemId = model.ConnectedSystemId,
        ConnectedSystemName = model.ConnectedSystemName,
        ObjectTypeName = model.ObjectTypeName,
        JoinType = model.JoinType,
        IsSource = model.IsSource,
        IsTarget = model.IsTarget,
        State = model.State,
        PendingAttributeChangeCount = model.PendingAttributeChangeCount,
        LastSynchronised = model.LastSynchronised,
        Join = JoinRecordDto.FromModel(model.Join),
        Scoping = model.Scoping.Select(ScopingExplanationDto.FromModel).ToList(),
        Conflicts = model.Conflicts.Select(ConnectionObjectTypeConflictDto.FromModel).ToList()
    };
}

/// <summary>
/// How a Connected System Object came to be joined: the method, the Synchronisation Rule responsible, when, and the
/// Activity that made the join while history still holds it.
/// </summary>
public class JoinRecordDto
{
    public ConnectedSystemObjectJoinType JoinType { get; set; }

    /// <summary>Projection, Provisioning, InboundMatching or ExportMatching; null when not recorded.</summary>
    public ConnectedSystemObjectJoinMethod? Method { get; set; }

    public DateTime? DateJoined { get; set; }

    /// <summary>The rule's id; null when not known, when no rule made the join, or when the rule has been deleted.</summary>
    public int? SyncRuleId { get; set; }

    /// <summary>The rule's name as it was when the join was made.</summary>
    public string? SyncRuleName { get; set; }

    /// <summary>Recorded (on the object when it joined), Derived (from Activity history) or NotRecorded.</summary>
    public JoinRecordSource Source { get; set; }

    /// <summary>How it joined and by which rule, in one sentence.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The Activity that made the join, while Activity history still holds it.</summary>
    public Guid? ActivityId { get; set; }

    public Guid? RunProfileExecutionItemId { get; set; }

    public static JoinRecordDto FromModel(JoinRecord model) => new()
    {
        JoinType = model.JoinType,
        Method = model.Method,
        DateJoined = model.DateJoined,
        SyncRuleId = model.SyncRuleId,
        SyncRuleName = model.SyncRuleName,
        Source = model.Source,
        Description = model.Description,
        ActivityId = model.ActivityId,
        RunProfileExecutionItemId = model.RunProfileExecutionItemId
    };
}

/// <summary>
/// Why an object is, or is not, in scope of one Synchronisation Rule: the outcome synchronisation reaches, and every
/// group and criterion as it evaluated.
/// </summary>
public class ScopingExplanationDto
{
    public int SyncRuleId { get; set; }

    public string SyncRuleName { get; set; } = string.Empty;

    public SyncRuleDirection Direction { get; set; }

    /// <summary>InScope, OutOfScope, or Undetermined where synchronisation would reach an invalid criterion.</summary>
    public ScopingRuleOutcome Outcome { get; set; }

    /// <summary>False when the rule has no scoping criteria, which puts every object of its type in scope.</summary>
    public bool HasCriteria { get; set; }

    public DateTime EvaluatedAt { get; set; }

    /// <summary>The outcome in one line without values, for example "Fails on Department"; empty when in scope.</summary>
    public string Hint { get; set; } = string.Empty;

    /// <summary>The top-level groups, ORed: one met group puts the object in scope.</summary>
    public List<ScopingGroupExplanationDto> Groups { get; set; } = [];

    /// <summary>
    /// Every criterion in the tree, flattened in evaluation order, so a client can filter them (for example the
    /// failing ones) without walking the groups. <see cref="ScopingCriterionExplanationDto.Path"/> locates each.
    /// </summary>
    public List<ScopingCriterionExplanationDto> Criteria { get; set; } = [];

    public static ScopingExplanationDto FromModel(ScopingExplanation model) => new()
    {
        SyncRuleId = model.SyncRuleId,
        SyncRuleName = model.SyncRuleName,
        Direction = model.Direction,
        Outcome = model.Outcome,
        HasCriteria = model.HasCriteria,
        EvaluatedAt = model.EvaluatedAt,
        Hint = model.Hint,
        Groups = model.Groups.Select(ScopingGroupExplanationDto.FromModel).ToList(),
        Criteria = model.Groups.SelectMany(AllCriteria).Select(ScopingCriterionExplanationDto.FromModel).ToList()
    };

    private static IEnumerable<ScopingCriterionExplanation> AllCriteria(ScopingGroupExplanation group) =>
        group.Criteria.Concat(group.ChildGroups.SelectMany(AllCriteria));
}

/// <summary>
/// How one scoping criteria group evaluated. Its children are its criteria followed by its child groups, the order
/// the evaluator takes them in and the order paths number them.
/// </summary>
public class ScopingGroupExplanationDto
{
    /// <summary>
    /// The top-level group's one-based position, then each child's position within its group, dot-separated, criteria
    /// counted before child groups (<c>1.3</c> is the third child of the first top-level group).
    /// </summary>
    public string Path { get; set; } = string.Empty;

    public SearchGroupType Type { get; set; }

    /// <summary>Whether the group is met; null when synchronisation would fail on an invalid criterion in it.</summary>
    public bool? Met { get; set; }

    public int MetCount { get; set; }

    public int ChildCount { get; set; }

    /// <summary>The group's line in the tree, for example "Any one of these (none met)".</summary>
    public string Description { get; set; } = string.Empty;

    public List<ScopingCriterionExplanationDto> Criteria { get; set; } = [];

    public List<ScopingGroupExplanationDto> ChildGroups { get; set; } = [];

    public static ScopingGroupExplanationDto FromModel(ScopingGroupExplanation model) => new()
    {
        Path = model.Path,
        Type = model.Type,
        Met = model.Met,
        MetCount = model.MetCount,
        ChildCount = model.ChildCount,
        Description = model.Description,
        Criteria = model.Criteria.Select(ScopingCriterionExplanationDto.FromModel).ToList(),
        ChildGroups = model.ChildGroups.Select(FromModel).ToList()
    };
}

/// <summary>
/// How one scoping criterion evaluated. Values of credential attributes are withheld (<see cref="Masked"/>).
/// </summary>
public class ScopingCriterionExplanationDto
{
    /// <summary>Where the criterion sits; see <see cref="ScopingGroupExplanationDto.Path"/>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>True when the criterion is met.</summary>
    public bool Met { get; set; }

    /// <summary>Met, NotMet, NoValue, AttributeMissing or Invalid.</summary>
    public ScopingCriterionOutcome Outcome { get; set; }

    public int? AttributeId { get; set; }

    public string? AttributeName { get; set; }

    public AttributeDataType? AttributeType { get; set; }

    public SearchComparisonType ComparisonType { get; set; }

    public bool CaseSensitive { get; set; }

    /// <summary>The value compared against, rendered; for a relative date, the boundary it resolved to.</summary>
    public string? Expected { get; set; }

    public DateCriteriaValueMode ValueMode { get; set; }

    /// <summary>A relative date criterion's offset in words, for example "30 days ago".</summary>
    public string? RelativeExpression { get; set; }

    /// <summary>The instant a relative date criterion resolved to, in UTC.</summary>
    public DateTime? ResolvedDate { get; set; }

    /// <summary>
    /// The value the outcome turned on, rendered: for a multi-valued attribute the value that decided it, otherwise the
    /// object's only value. Null when it held none, or held several and no single one decided.
    /// </summary>
    public string? Actual { get; set; }

    /// <summary>True when the values are withheld because the attribute may hold a credential.</summary>
    public bool Masked { get; set; }

    /// <summary>How many values the object holds for the attribute; scoping compares every one.</summary>
    public int ValueCount { get; set; }

    /// <summary>The criterion as a condition, for example "Department equals Finance".</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>What the object held, for example "is Engineering" or "has no value".</summary>
    public string ActualDescription { get; set; } = string.Empty;

    public static ScopingCriterionExplanationDto FromModel(ScopingCriterionExplanation model) => new()
    {
        Path = model.Path,
        Met = model.Outcome == ScopingCriterionOutcome.Met,
        Outcome = model.Outcome,
        AttributeId = model.AttributeId,
        AttributeName = model.AttributeName,
        AttributeType = model.AttributeType,
        ComparisonType = model.ComparisonType,
        CaseSensitive = model.CaseSensitive,
        Expected = model.ExpectedDisplay,
        ValueMode = model.ValueMode,
        RelativeExpression = model.RelativeDisplay,
        ResolvedDate = model.ResolvedDate,
        Actual = model.ActualDisplay,
        Masked = model.Masked,
        ValueCount = model.ValueCount,
        Description = model.Description,
        ActualDescription = model.ActualDescription
    };
}

/// <summary>
/// An enabled export Synchronisation Rule that cannot connect because the connection holds the Metaverse Object's one
/// slot in the Connected System with an object of another type.
/// </summary>
public class ConnectionObjectTypeConflictDto
{
    public int SyncRuleId { get; set; }

    public string SyncRuleName { get; set; } = string.Empty;

    public string TargetObjectTypeName { get; set; } = string.Empty;

    public string ExistingObjectTypeName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>The rule's scoping against the Metaverse Object now.</summary>
    public ScopingExplanationDto Scoping { get; set; } = new();

    public static ConnectionObjectTypeConflictDto FromModel(ConnectionObjectTypeConflict model) => new()
    {
        SyncRuleId = model.SyncRuleId,
        SyncRuleName = model.SyncRuleName,
        TargetObjectTypeName = model.TargetObjectTypeName,
        ExistingObjectTypeName = model.ExistingObjectTypeName,
        Description = model.Description,
        Scoping = ScopingExplanationDto.FromModel(model.Scoping)
    };
}

/// <summary>
/// An enabled export Synchronisation Rule whose Connected System holds no object joined to the Metaverse Object, and
/// the one reason why.
/// </summary>
public class NotConnectedEntryDto
{
    public int ConnectedSystemId { get; set; }

    public string ConnectedSystemName { get; set; } = string.Empty;

    public ConnectedSystemStatus ConnectedSystemStatus { get; set; }

    public int SyncRuleId { get; set; }

    public string SyncRuleName { get; set; } = string.Empty;

    /// <summary>The Connected System Object Type the rule would provision or join.</summary>
    public string ObjectTypeName { get; set; } = string.Empty;

    /// <summary>NotInScope, ProvisioningDisabled, RuleMisconfigured or NotYetProvisioned.</summary>
    public NotConnectedReason Reason { get; set; }

    /// <summary>A one-line qualifier, for example "Fails on Department; Cost Centre or Job Title".</summary>
    public string Hint { get; set; } = string.Empty;

    /// <summary>What the bullets answer, for example "To come into scope" or "What happens next".</summary>
    public string BulletsTitle { get; set; } = string.Empty;

    public List<ExplanationBulletDto> Bullets { get; set; } = [];

    /// <summary>The whole entry as plain text, to paste into a message or ticket.</summary>
    public string Summary { get; set; } = string.Empty;

    public ScopingExplanationDto Scoping { get; set; } = new();

    public static NotConnectedEntryDto FromModel(NotConnectedEntry model) => new()
    {
        ConnectedSystemId = model.ConnectedSystemId,
        ConnectedSystemName = model.ConnectedSystemName,
        ConnectedSystemStatus = model.ConnectedSystemStatus,
        SyncRuleId = model.SyncRuleId,
        SyncRuleName = model.SyncRuleName,
        ObjectTypeName = model.ObjectTypeName,
        Reason = model.Reason,
        Hint = model.Hint,
        BulletsTitle = model.BulletsTitle,
        Bullets = model.Bullets.Select(ExplanationBulletDto.FromModel).ToList(),
        Summary = model.Summary,
        Scoping = ScopingExplanationDto.FromModel(model.Scoping)
    };
}

/// <summary>
/// One line of a list of what an object needs or what happens next: as plain text with text values quoted, and in
/// typed segments for a client that styles values instead.
/// </summary>
public class ExplanationBulletDto
{
    public string Text { get; set; } = string.Empty;

    public List<ExplanationSegmentDto> Segments { get; set; } = [];

    public static ExplanationBulletDto FromModel(ExplanationBullet model) => new()
    {
        Text = model.PlainText,
        Segments = model.Segments.Select(s => new ExplanationSegmentDto { Kind = s.Kind, Text = s.Text }).ToList()
    };
}

/// <summary>One piece of a bullet: Text, Attribute, ExpectedValue, CurrentValue, NoValue or Hidden.</summary>
public class ExplanationSegmentDto
{
    public ExplanationSegmentKind Kind { get; set; }

    public string Text { get; set; } = string.Empty;
}
