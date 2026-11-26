// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>
/// What changing an attribute's contribution mode would do to the values Metaverse Objects hold, as each is next
/// synchronised. Where values would be removed the counts are a floor: a value a joined contributor no longer supplies
/// because it changed at its source is not counted until that contributor is next synchronised.
/// </summary>
public class ContributionModeImpactDto
{
    /// <summary>The attribute.</summary>
    public int AttributeId { get; set; }

    /// <summary>The attribute's name.</summary>
    public string AttributeName { get; set; } = string.Empty;

    /// <summary>The attribute's contribution mode today.</summary>
    public AttributeContributionMode CurrentMode { get; set; }

    /// <summary>The contribution mode being considered.</summary>
    public AttributeContributionMode TargetMode { get; set; }

    /// <summary>How many Metaverse Objects would lose one or more values.</summary>
    public int ObjectsLosingValues { get; set; }

    /// <summary>How many values would be removed because no contributor supplies them any more; only leaving Accumulate removes these.</summary>
    public int ValuesNoLongerSupplied { get; set; }

    /// <summary>How many Metaverse Objects hold values from several contributors and would keep only the highest-priority one's; only moving to Priority does this.</summary>
    public int ObjectsKeepingOneContributor { get; set; }

    /// <summary>How many Metaverse Objects would gain values from contributors they hold no values from today; only moving from Priority does this.</summary>
    public int ObjectsGainingValues { get; set; }

    /// <summary>Whether the change must be confirmed when it is made (with <c>confirmLeavingAccumulate</c>): true when leaving Accumulate.</summary>
    public bool RequiresConfirmation { get; set; }

    /// <summary>Builds the DTO from the model.</summary>
    public static ContributionModeImpactDto FromModel(ContributionModeImpact impact) => new()
    {
        AttributeId = impact.AttributeId,
        AttributeName = impact.AttributeName,
        CurrentMode = impact.CurrentMode,
        TargetMode = impact.TargetMode,
        ObjectsLosingValues = impact.ObjectsLosingValues,
        ValuesNoLongerSupplied = impact.ValuesNoLongerSupplied,
        ObjectsKeepingOneContributor = impact.ObjectsKeepingOneContributor,
        ObjectsGainingValues = impact.ObjectsGainingValues,
        RequiresConfirmation = impact.RequiresConfirmation
    };
}
