// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging.DTOs;

namespace JIM.Models.Staging;

/// <summary>
/// A Run Profile's enabled state (#2740): the one wording of why a Run Profile is disabled and why a run of it is
/// refused, shared by the queue, the Worker, the portal and the REST API so that every surface says the same thing; and
/// the rule for enabling one again.
/// </summary>
/// <remarks>
/// User-facing text writes "partition" in lower case, as the run refusals for a deselected partition already do.
/// </remarks>
public static class RunProfileEnablement
{
    /// <summary>
    /// Why a Run Profile is disabled, as a sentence; null while it is enabled.
    /// </summary>
    public static string? DescribeWhyDisabled(this ConnectedSystemRunProfile runProfile)
    {
        ArgumentNullException.ThrowIfNull(runProfile);

        if (runProfile.Enabled)
            return null;

        return runProfile.DisabledReason switch
        {
            ConnectedSystemRunProfileDisabledReason.PartitionRemoved =>
                $"A hierarchy refresh removed the partition it targeted, '{runProfile.DisabledPartitionName}', because the Connected System no longer has it.",
            _ => "An administrator disabled it."
        };
    }

    /// <summary>
    /// Why a run of a disabled Run Profile is refused, naming the Run Profile, the reason and what to do about it.
    /// </summary>
    public static string DescribeRunRefusal(ConnectedSystemRunProfile runProfile)
    {
        ArgumentNullException.ThrowIfNull(runProfile);

        var remedy = runProfile.NeedsScopeToEnable()
            ? "Enable it again, choosing the partition it reads, to run it."
            : "Enable it again to run it.";

        return $"Run Profile '{runProfile.Name}' is disabled, so it cannot run. {runProfile.DescribeWhyDisabled()} {remedy}";
    }

    /// <summary>
    /// Whether enabling this Run Profile needs the change to state what it reads: true for one a hierarchy refresh
    /// disabled because it removed the Partition the Run Profile targeted. Its Partition was cleared, so enabling it with
    /// nothing chosen would have it read every selected Partition.
    /// </summary>
    public static bool NeedsScopeToEnable(this ConnectedSystemRunProfile runProfile)
    {
        ArgumentNullException.ThrowIfNull(runProfile);
        return !runProfile.Enabled && runProfile.DisabledReason == ConnectedSystemRunProfileDisabledReason.PartitionRemoved;
    }

    /// <summary>
    /// Applies an update's enabled state to <paramref name="updated"/>, judged against the state that is stored, and
    /// returns why the update is refused, or null when it may be saved.
    /// </summary>
    /// <remarks>
    /// The reason a Run Profile is disabled is JIM's to record, never the caller's: an enabled Run Profile has none;
    /// one newly disabled was disabled by hand; one that stays disabled keeps the reason stored. Enabling a Run Profile
    /// that a removed Partition disabled is refused unless the result targets a Partition or the caller states that it
    /// reads all selected partitions. A Partition chosen while it stayed disabled counts: an administrator picked it
    /// after the removal, so nothing widens.
    /// </remarks>
    /// <param name="stored">The stored state, or null where there is none to judge against (a caller that holds no
    /// stored record), in which case <paramref name="updated"/>'s state is taken as given.</param>
    /// <param name="updated">The Run Profile as the update would save it. Its reason fields, and its Partition when
    /// <paramref name="allSelectedPartitions"/> is true, are set here.</param>
    /// <param name="allSelectedPartitions">The caller's explicit statement that the Run Profile reads every selected
    /// partition. Clears any Partition it targets.</param>
    public static string? ApplyEnabledStateChange(ConnectedSystemRunProfileEnabledState? stored, ConnectedSystemRunProfile updated, bool allSelectedPartitions)
    {
        ArgumentNullException.ThrowIfNull(updated);

        if (updated.Enabled && stored is { Enabled: false, DisabledReason: ConnectedSystemRunProfileDisabledReason.PartitionRemoved } &&
            updated.Partition == null && !allSelectedPartitions)
        {
            return $"Run Profile '{updated.Name}' was disabled because a hierarchy refresh removed the partition it targeted, " +
                   $"'{stored.DisabledPartitionName}'. To enable it, choose the partition it reads, or choose all selected partitions.";
        }

        if (allSelectedPartitions)
            updated.Partition = null;

        if (updated.Enabled)
        {
            updated.DisabledReason = null;
            updated.DisabledPartitionName = null;
        }
        else if (stored is { Enabled: false })
        {
            updated.DisabledReason = stored.DisabledReason ?? ConnectedSystemRunProfileDisabledReason.Manual;
            updated.DisabledPartitionName = stored.DisabledPartitionName;
        }
        else if (stored != null || updated.DisabledReason == null)
        {
            updated.DisabledReason = ConnectedSystemRunProfileDisabledReason.Manual;
            updated.DisabledPartitionName = null;
        }

        return null;
    }
}
