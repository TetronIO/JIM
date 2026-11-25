// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Application.Servers;

/// <summary>
/// The impact report shown before a Metaverse attribute's contribution mode is changed (#3059, Phase 3; PRD
/// requirements 16 and 17). Kept as its own partial file.
/// </summary>
public partial class MetaverseServer
{
    /// <summary>
    /// What changing the attribute's contribution mode to <paramref name="targetMode"/> would do to the values Metaverse
    /// Objects hold, as each is next synchronised; null when there is no such attribute. A mode the attribute cannot use
    /// is refused as saving it would be.
    /// </summary>
    /// <exception cref="Exceptions.UnsupportedContributionModeException">The attribute's data type or plurality cannot use the mode.</exception>
    public async Task<ContributionModeImpact?> GetContributionModeImpactAsync(int attributeId, AttributeContributionMode targetMode)
    {
        var attribute = await Application.Repository.Metaverse.GetMetaverseAttributeAsync(attributeId);
        if (attribute == null)
            return null;

        EnsureContributionModeCompatible(targetMode, attribute.Type, attribute.AttributePlurality);

        var counts = attribute.ContributionMode == targetMode
            ? new ContributionModeImpactCounts(0, 0, 0, 0, 0)
            : await Application.Repository.Metaverse.GetContributionModeImpactCountsAsync(attributeId);
        return ContributionModeImpact.From(attribute.Id, attribute.Name, attribute.ContributionMode, targetMode, counts);
    }
}
