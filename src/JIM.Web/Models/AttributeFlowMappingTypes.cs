// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Logic;

namespace JIM.Web.Models;

/// <summary>
/// Which Mapping Type an Attribute Flow is filtered under on the Attribute Flow tab (Generated Value Forms, #2014): so
/// generated values and Derived Attribute Flows can be picked out together or apart, an import Expression that reads the
/// Metaverse counts as Derived rather than as an ordinary Expression. The categories never overlap.
/// </summary>
public static class AttributeFlowMappingTypes
{
    /// <summary>
    /// <paramref name="mapping"/>'s category. A generated value is Generated even when its base expression reads the
    /// Metaverse; an export Expression is never Derived, because every export expression reads the Metaverse.
    /// </summary>
    public static AttributeFlowMappingType Of(SyncRuleMapping mapping, SyncRuleDirection direction)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var sourceType = mapping.GetSourceType();
        if (sourceType == SyncRuleMappingSourcesType.ExpressionMapping && direction == SyncRuleDirection.Import && DerivedFlowGraph.ReadsMetaverse(mapping))
            return AttributeFlowMappingType.Derived;

        return sourceType switch
        {
            SyncRuleMappingSourcesType.AttributeMapping => AttributeFlowMappingType.AttributeMapping,
            SyncRuleMappingSourcesType.ExpressionMapping => AttributeFlowMappingType.ExpressionMapping,
            SyncRuleMappingSourcesType.GeneratedMapping => AttributeFlowMappingType.GeneratedMapping,
            SyncRuleMappingSourcesType.AdvancedMapping => AttributeFlowMappingType.AdvancedMapping,
            _ => AttributeFlowMappingType.NotSet
        };
    }
}
