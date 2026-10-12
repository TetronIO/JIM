// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
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

    /// <summary>
    /// A finding split around the Container or partition it names, so the identifier can be set in the same code style
    /// as the values it is drawn from. A finding that does not contain the identifier comes back whole.
    /// </summary>
    /// <param name="finding">The finding, as the diagnosis states it.</param>
    /// <param name="identifier">The Container or partition the cause concerns, or null.</param>
    public static (string Before, string? Identifier, string After) SplitFinding(string finding, string? identifier)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var index = string.IsNullOrEmpty(identifier) ? -1 : finding.IndexOf(identifier, StringComparison.Ordinal);
        return index < 0
            ? (finding, null, string.Empty)
            : (finding[..index], identifier, finding[(index + identifier!.Length)..]);
    }

    /// <summary>
    /// The next steps grouped by where they are taken, JIM's configuration first: what an administrator can change
    /// themselves comes before what needs the Connected System's owners. Each group keeps the steps' own order.
    /// </summary>
    public static IReadOnlyList<UnresolvedReferenceFixGroup> GroupFixes(IEnumerable<UnresolvedReferenceFix> fixes) =>
    [
        .. fixes
            .GroupBy(fix => fix.FixLocation)
            .OrderBy(group => group.Key == UnresolvedReferenceFixLocation.Jim ? 0 : 1)
            .Select(group => new UnresolvedReferenceFixGroup(group.Key, [.. group]))
    ];

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

    /// <summary>The Connector Space's query value that filters it to objects holding unresolved references.</summary>
    public const string UnresolvedReferencesFilterValue = "unresolved";

    /// <summary>
    /// The Connected System page's Unresolved References button: amber where Unresolved Reference Handling asks for
    /// them to be reported, as Pending Exports is when exports are waiting, and neutral under Ignore, which stops runs
    /// reporting them without hiding them from someone looking.
    /// </summary>
    public static UnresolvedReferenceTone SignpostTone(UnresolvedReferenceHandling handling) =>
        handling == UnresolvedReferenceHandling.Ignore ? UnresolvedReferenceTone.None : UnresolvedReferenceTone.Warning;

    /// <summary>What the Connected System page's Unresolved References button says when hovered.</summary>
    public static string SignpostTooltip(ConnectedSystemUnresolvedReferenceTotals totals, UnresolvedReferenceHandling handling)
    {
        ArgumentNullException.ThrowIfNull(totals);

        var handlingSentence = handling == UnresolvedReferenceHandling.Ignore
            ? "Unresolved Reference Handling is set to Ignore, so runs do not report them."
            : $"Unresolved Reference Handling is set to {handling}.";

        return $"{ReferencesOnObjectsText(totals.ReferenceCount, totals.ObjectCount)} could not be resolved. {handlingSentence}";
    }

    /// <summary>How many references on how many objects, as the signpost and "Where they point" both put it.</summary>
    public static string ReferencesOnObjectsText(int referenceCount, int objectCount) =>
        $"{referenceCount:N0} {(referenceCount == 1 ? "reference" : "references")} on " +
        $"{objectCount:N0} {(objectCount == 1 ? "Connected System Object" : "Connected System Objects")}";

    /// <summary>The Connected System's Connector Space, filtered to the objects holding unresolved references.</summary>
    public static string ConnectorSpaceHref(int connectedSystemId) =>
        $"/admin/connected-systems/{connectedSystemId}/connector-space?references={UnresolvedReferencesFilterValue}";

    /// <summary>
    /// Whether a Connector Space link asks for the unresolved references filter. Anything else is ignored rather than
    /// refused: a stale or hand-typed link should open the list, not an error page.
    /// </summary>
    public static bool IsUnresolvedReferencesFilter(string? value) =>
        string.Equals(value, UnresolvedReferencesFilterValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>How many objects a step's values sit on, as "Where they point" puts it beside the step.</summary>
    public static string ObjectCountText(int count) =>
        $"on {count:N0} {(count == 1 ? "object" : "objects")}";
}
