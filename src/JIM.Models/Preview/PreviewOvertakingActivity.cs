// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Preview;

/// <summary>
/// An Activity recorded since a preview started that could change the preview's answer (#2022): enough of it to say
/// what happened, read when the preview's staleness is judged.
/// </summary>
/// <param name="ActivityId">The Activity.</param>
/// <param name="Created">When it was recorded.</param>
/// <param name="TargetType">What it acted on.</param>
/// <param name="TargetOperationType">What it did.</param>
/// <param name="TargetName">The name of what it acted on, as recorded: a Run Profile's name for a run.</param>
/// <param name="TargetContext">Where that was, as recorded: a run's Connected System.</param>
/// <param name="ConnectedSystemId">The Connected System it acted on or in, where it recorded one.</param>
/// <param name="SyncRuleId">The Synchronisation Rule it acted on, where it recorded one.</param>
public sealed record PreviewOvertakingActivity(
    Guid ActivityId,
    DateTime Created,
    ActivityTargetType TargetType,
    ActivityTargetOperationType TargetOperationType,
    string? TargetName,
    string? TargetContext,
    int? ConnectedSystemId = null,
    int? SyncRuleId = null)
{
    /// <summary>
    /// What happened, as a clause naming what it happened to ("Run Profile 'Delta Import' ran on Connected System 'HR
    /// Import'"). It names things itself rather than pointing at the Activity, because it is snapshotted onto the
    /// Activity of a change that cites an out-of-date preview, and retention removes the Activity it describes first.
    /// </summary>
    public string Describe() => string.Concat(Parts().Select(p => p.ToText()));

    /// <summary>
    /// What happened, in pieces: plain text and the things it names, so a surface can show each named thing as that
    /// thing (the portal renders them as object chips linking to the run, the Connected System or the rule). Read as
    /// text, the pieces are exactly <see cref="Describe"/>.
    /// </summary>
    public IReadOnlyList<PreviewOvertakingPart> Parts() => (TargetType, TargetOperationType) switch
    {
        (ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute) => TargetContext is { Length: > 0 } system
            ? [RunProfile(), Text(" ran on "), Named(PreviewOvertakingPartKind.ConnectedSystem, "Connected System", system, ConnectedSystemId)]
            : [RunProfile(), Text(" ran")],
        (ActivityTargetType.MetaverseObjectHousekeeping, ActivityTargetOperationType.Execute) => [Text("Metaverse Object housekeeping ran")],
        (ActivityTargetType.TemporalScopeReconciliation, ActivityTargetOperationType.Execute) => [Text("Temporal Scope Reconciliation ran")],
        (ActivityTargetType.DataGeneration, ActivityTargetOperationType.Execute) => [Text("Example Data was generated")],
        (ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Clear) =>
            [Text("the Connector Space of "), Target(), Text(" was cleared")],
        (ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.SchemaRefreshRemoval) =>
            [Text("a schema refresh of "), Target(), Text(" removed data")],
        (ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Deprovision) => [Target(), Text(" was deprovisioned")],
        (ActivityTargetType.SynchronisationRule, ActivityTargetOperationType.RecallAttributeValues) =>
            [Target(), Text(" recalled its attribute values")],
        _ => [Target(), Text($" was {PastTense(TargetOperationType)}")]
    };

    private static PreviewOvertakingPart Text(string text) => new(PreviewOvertakingPartKind.Text, text);

    private PreviewOvertakingPart RunProfile() =>
        Named(PreviewOvertakingPartKind.RunProfile, "Run Profile", TargetName, activityId: ActivityId);

    /// <summary>The thing this Activity acted on, as the kind of thing it is.</summary>
    private PreviewOvertakingPart Target() => TargetType switch
    {
        ActivityTargetType.ConnectedSystem => Named(PreviewOvertakingPartKind.ConnectedSystem, "Connected System", TargetName, ConnectedSystemId),
        ActivityTargetType.SynchronisationRule => Named(PreviewOvertakingPartKind.SynchronisationRule, "Synchronisation Rule", TargetName, SyncRuleId),
        ActivityTargetType.ConnectedSystemRunProfile => RunProfile(),
        _ => Named(PreviewOvertakingPartKind.Other, TypeName(TargetType), TargetName)
    };

    /// <summary>A named thing, or "a Run Profile" and the like where no name was recorded.</summary>
    private static PreviewOvertakingPart Named(PreviewOvertakingPartKind kind, string typeName, string? name, int? entityId = null,
        Guid? activityId = null) =>
        name is { Length: > 0 }
            ? new PreviewOvertakingPart(kind, name, typeName, entityId, activityId)
            : Text($"a {typeName}");

    /// <summary>
    /// The thing an Activity acted on, as JIM names it: its target type, split into words, which is how every domain
    /// noun reads, except where the type's name is not the noun.
    /// </summary>
    private static string TypeName(ActivityTargetType targetType) => targetType switch
    {
        ActivityTargetType.ConnectedSystemRunProfile => "Run Profile",
        ActivityTargetType.NotSet => "configuration item",
        _ => string.Concat(targetType.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? $" {c}" : c.ToString()))
    };

    private static string PastTense(ActivityTargetOperationType operation) => operation switch
    {
        ActivityTargetOperationType.Create => "created",
        ActivityTargetOperationType.Update => "updated",
        ActivityTargetOperationType.Delete => "deleted",
        ActivityTargetOperationType.Revert => "reverted",
        ActivityTargetOperationType.Reset => "reset",
        _ => "changed"
    };
}
