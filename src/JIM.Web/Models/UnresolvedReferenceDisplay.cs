// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;
using MudBlazor;

namespace JIM.Web.Models;

/// <summary>
/// What the Unresolved References section says and links to (#959), on a Run Profile Execution Item's page and on a
/// Connected System Object's page alike: one place, so the two cannot describe the same reference two ways.
/// </summary>
public static class UnresolvedReferenceDisplay
{
    /// <summary>
    /// Red where the run counted the references as the item's error, including an item recorded before the count
    /// existed; amber where it left references unresolved without doing so; nothing where it left none.
    /// </summary>
    public static UnresolvedReferenceTone ToneFor(ActivityRunProfileExecutionItemErrorType? errorType, int? unresolvedReferenceCount)
    {
        if (errorType == ActivityRunProfileExecutionItemErrorType.UnresolvedReference)
            return UnresolvedReferenceTone.Error;

        return unresolvedReferenceCount is > 0 ? UnresolvedReferenceTone.Warning : UnresolvedReferenceTone.None;
    }

    /// <summary>
    /// How the object's unresolved references stand now against the number the run left.
    /// </summary>
    /// <param name="runCount">How many the run left; null for an item recorded before the count existed.</param>
    /// <param name="nowCount">How many the object holds now.</param>
    public static UnresolvedReferenceSinceRun SinceRun(int? runCount, int nowCount)
    {
        if (nowCount == 0)
            return UnresolvedReferenceSinceRun.NoneNow;

        if (runCount is not { } run)
            return UnresolvedReferenceSinceRun.RunCountUnknown;

        if (nowCount == run)
            return UnresolvedReferenceSinceRun.Unchanged;

        return nowCount < run ? UnresolvedReferenceSinceRun.Fewer : UnresolvedReferenceSinceRun.More;
    }

    /// <summary>The likely cause's label, for chips and table cells.</summary>
    public static string CauseLabel(UnresolvedReferenceCause cause) => cause switch
    {
        UnresolvedReferenceCause.NotFound => "Not found",
        UnresolvedReferenceCause.NotFoundInScope => "Not found in scope",
        UnresolvedReferenceCause.ContainerNotInScope => "Container not in scope",
        UnresolvedReferenceCause.ContainerExcluded => "Container excluded",
        UnresolvedReferenceCause.PartitionNotSelected => "Partition not selected",
        UnresolvedReferenceCause.OutsideConnectedSystem => "Outside this Connected System",
        UnresolvedReferenceCause.AmbiguousObjectType => "Ambiguous Object Type",
        _ => cause.ToString()
    };

    /// <summary>
    /// The likely cause's chip colour: amber where JIM's configuration can fix it, neutral where the fix lies in the
    /// Connected System.
    /// </summary>
    public static Color CauseColour(UnresolvedReferenceFixLocation fixLocation) =>
        fixLocation == UnresolvedReferenceFixLocation.Jim ? Color.Warning : Color.Default;

    /// <summary>Where the fix lies, as a chip label.</summary>
    public static string FixLocationLabel(UnresolvedReferenceFixLocation fixLocation) =>
        fixLocation == UnresolvedReferenceFixLocation.Jim ? "JIM" : "Source system";

    /// <summary>Where the fix lies, as a chip colour.</summary>
    public static Color FixLocationColour(UnresolvedReferenceFixLocation fixLocation) =>
        fixLocation == UnresolvedReferenceFixLocation.Jim ? Color.Primary : Color.Default;

    /// <summary>
    /// The Connected System tab where a cause is acted on: Container Scope for the scope causes, the Object Type's
    /// schema for an ambiguous target, the settings (where Unresolved Reference Handling lives) for a reference outside
    /// the Connected System, and the Run Profiles for a value that a Full Import may yet resolve.
    /// </summary>
    /// <param name="cause">The likely cause.</param>
    /// <param name="connectedSystemId">The Connected System the reference belongs to.</param>
    /// <param name="objectTypeName">The referencing object's Object Type, which the schema link opens on; null opens the
    /// schema without choosing one.</param>
    public static UnresolvedReferenceAction ActionFor(UnresolvedReferenceCause cause, int connectedSystemId, string? objectTypeName)
    {
        var connectedSystemHref = $"/admin/connected-systems/{connectedSystemId}";
        return cause switch
        {
            UnresolvedReferenceCause.ContainerNotInScope
                or UnresolvedReferenceCause.ContainerExcluded
                or UnresolvedReferenceCause.PartitionNotSelected => new("Review Container Scope", $"{connectedSystemHref}?t=scope"),
            UnresolvedReferenceCause.AmbiguousObjectType => new("View Schema", string.IsNullOrEmpty(objectTypeName)
                ? $"{connectedSystemHref}?t=schema"
                : $"{connectedSystemHref}?t=schema&ot={Uri.EscapeDataString(objectTypeName)}"),
            UnresolvedReferenceCause.OutsideConnectedSystem => new("View Settings", $"{connectedSystemHref}?t=settings"),
            _ => new("View Run Profiles", $"{connectedSystemHref}?t=run-profiles")
        };
    }
}
