// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// Which contribution modes an attribute can use (#3059), shared by every surface that sets one, so the server's
/// validation and the portal's editor cannot disagree.
/// </summary>
/// <remarks>
/// Only Priority is meaningful for a single-valued attribute, which only ever holds one value. Accumulate is refused for a
/// Reference attribute because a reference is deleted with the Metaverse Object it points to (#1019), which would break
/// Accumulate's promise that values are kept; a text attribute recording the source's identifier for the reference is the
/// supported alternative.
/// </remarks>
public static class AttributeContributionModeRules
{
    /// <summary>
    /// Whether an attribute of this data type and plurality can use the mode.
    /// </summary>
    public static bool IsPermitted(AttributeContributionMode mode, AttributeDataType type, AttributePlurality plurality) => mode switch
    {
        AttributeContributionMode.Priority => true,
        AttributeContributionMode.Accumulate => plurality == AttributePlurality.MultiValued && type != AttributeDataType.Reference,
        _ => false
    };

    /// <summary>
    /// The mode an attribute of this data type and plurality can hold: the requested one where permitted, otherwise
    /// Priority. Used by an editor when the administrator changes the type or plurality under a chosen mode.
    /// </summary>
    public static AttributeContributionMode Constrain(AttributeContributionMode mode, AttributeDataType type, AttributePlurality plurality) =>
        IsPermitted(mode, type, plurality) ? mode : AttributeContributionMode.Priority;

    /// <summary>
    /// Whether changing the mode can remove values the attribute holds: true when leaving Accumulate, since the values it
    /// kept from contributors that no longer supply them are no longer kept. Every surface asks for confirmation first.
    /// </summary>
    public static bool CanRemoveValues(AttributeContributionMode from, AttributeContributionMode to) =>
        from == AttributeContributionMode.Accumulate && to != AttributeContributionMode.Accumulate;
}
