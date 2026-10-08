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
public sealed record PreviewOvertakingActivity(
    Guid ActivityId,
    DateTime Created,
    ActivityTargetType TargetType,
    ActivityTargetOperationType TargetOperationType,
    string? TargetName,
    string? TargetContext)
{
    /// <summary>
    /// What happened, as a clause naming what it happened to ("Run Profile 'Delta Import' ran on Connected System 'HR
    /// Import'"). It names things itself rather than pointing at the Activity, because it is snapshotted onto the
    /// Activity of a change that cites an out-of-date preview, and retention removes the Activity it describes first.
    /// </summary>
    public string Describe() => (TargetType, TargetOperationType) switch
    {
        (ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute) =>
            TargetContext is { Length: > 0 } system
                ? $"{Named("Run Profile")} ran on Connected System '{system}'"
                : $"{Named("Run Profile")} ran",
        (ActivityTargetType.MetaverseObjectHousekeeping, ActivityTargetOperationType.Execute) => "Metaverse Object housekeeping ran",
        (ActivityTargetType.TemporalScopeReconciliation, ActivityTargetOperationType.Execute) => "Temporal Scope Reconciliation ran",
        (ActivityTargetType.DataGeneration, ActivityTargetOperationType.Execute) => "Example Data was generated",
        (ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Clear) => $"the Connector Space of {Named("Connected System")} was cleared",
        (ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.SchemaRefreshRemoval) =>
            $"a schema refresh of {Named("Connected System")} removed data",
        (ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Deprovision) => $"{Named("Connected System")} was deprovisioned",
        (ActivityTargetType.SynchronisationRule, ActivityTargetOperationType.RecallAttributeValues) =>
            $"{Named("Synchronisation Rule")} recalled its attribute values",
        _ => $"{Named(TypeName(TargetType))} was {PastTense(TargetOperationType)}"
    };

    /// <summary>The kind of thing, followed by its name where one was recorded.</summary>
    private string Named(string kind) => TargetName is { Length: > 0 } name ? $"{kind} '{name}'" : $"a {kind}";

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
