// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// What changing a Metaverse attribute's contribution mode would do to the values Metaverse Objects hold (#3059), as each
/// object is next synchronised. The counts are read from what JIM holds now, without evaluating any Attribute Flow, so
/// they are a floor where values would be removed: a value a joined contributor no longer supplies (changed at its
/// source) is not counted until that contributor is next synchronised.
/// </summary>
public sealed class ContributionModeImpact
{
    /// <summary>The attribute.</summary>
    public int AttributeId { get; init; }

    /// <summary>The attribute's name.</summary>
    public string AttributeName { get; init; } = string.Empty;

    /// <summary>The attribute's contribution mode today.</summary>
    public AttributeContributionMode CurrentMode { get; init; }

    /// <summary>The contribution mode being considered.</summary>
    public AttributeContributionMode TargetMode { get; init; }

    /// <summary>How many Metaverse Objects would lose one or more values.</summary>
    public int ObjectsLosingValues { get; init; }

    /// <summary>How many values would be removed because no contributor supplies them any more (leaving Accumulate).</summary>
    public int ValuesNoLongerSupplied { get; init; }

    /// <summary>How many Metaverse Objects would keep only their highest-priority contributor's values (moving to Priority).</summary>
    public int ObjectsKeepingOneContributor { get; init; }

    /// <summary>How many Metaverse Objects would gain values from contributors whose values they do not hold today.</summary>
    public int ObjectsGainingValues { get; init; }

    /// <summary>Whether the change must be confirmed before it is saved: true when leaving Accumulate.</summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>
    /// The impact of moving from <paramref name="currentMode"/> to <paramref name="targetMode"/>, read from
    /// <paramref name="counts"/>.
    /// </summary>
    public static ContributionModeImpact From(int attributeId, string attributeName, AttributeContributionMode currentMode,
        AttributeContributionMode targetMode, ContributionModeImpactCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        var leavingAccumulate = AttributeContributionModeRules.CanRemoveValues(currentMode, targetMode);
        var toPriority = currentMode != AttributeContributionMode.Priority && targetMode == AttributeContributionMode.Priority;
        var fromPriority = currentMode == AttributeContributionMode.Priority && targetMode != AttributeContributionMode.Priority;

        var objectsLosing = toPriority
            ? counts.ObjectsLosingValuesUnderPriority
            : leavingAccumulate ? counts.ObjectsWithValuesNoLongerSupplied : 0;

        return new ContributionModeImpact
        {
            AttributeId = attributeId,
            AttributeName = attributeName,
            CurrentMode = currentMode,
            TargetMode = targetMode,
            ObjectsLosingValues = objectsLosing,
            ValuesNoLongerSupplied = leavingAccumulate ? counts.ValuesNoLongerSupplied : 0,
            ObjectsKeepingOneContributor = toPriority ? counts.ObjectsWithSeveralContributors : 0,
            ObjectsGainingValues = fromPriority ? counts.ObjectsThatMayGainValues : 0,
            RequiresConfirmation = leavingAccumulate
        };
    }
}
