// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Application.Services;

/// <summary>
/// The one decision every recall path makes before it withdraws a contributed Metaverse Object value (#3059). A value of
/// an Accumulate attribute is never withdrawn by synchronisation, whatever happens to its contributor: obsoletion, scope
/// exit, disconnection, a rule or mapping deleted, or its Connected System deleted. Every path that selects values to
/// withdraw by their contributor filters through this, so a new path inherits the rule by calling it.
/// </summary>
public static class ContributedValueRecall
{
    /// <summary>
    /// Whether a contributed value may be withdrawn when its contributor leaves: false for a value of an Accumulate
    /// attribute, true otherwise.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value's attribute is not loaded, so its contribution mode cannot be
    /// read. Guessing either way is wrong (withdrawing would delete values Accumulate exists to keep; keeping would leave a
    /// departed source's values behind), so the caller's object fails instead.</exception>
    public static bool IsRecallable(MetaverseObjectAttributeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Attribute == null)
            throw new InvalidOperationException(
                $"Metaverse Object attribute value {value.Id} was loaded without its attribute ({value.AttributeId}), so whether it may be " +
                "recalled cannot be decided. The query that loaded it must include the attribute.");

        return value.Attribute.ContributionMode != AttributeContributionMode.Accumulate;
    }
}
