// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Application.Services;

/// <summary>
/// Resolves the winning value for a Metaverse Object attribute when multiple import Synchronisation Rules
/// contribute to it (#91). This class hosts the deterministic, side-effect-free resolution core: given the
/// contributions to one attribute (each already evaluated to a tri-state), it selects the winner by priority,
/// honouring "Null is a value". Gathering the contributions (loading joined Connected System Objects, evaluating
/// scoping and mappings) and persisting the result are engine responsibilities layered on top of this core.
/// </summary>
public class AttributePriorityService
{
    /// <summary>
    /// Resolves a Metaverse Object attribute from its contributing import mappings.
    /// Evaluation order is by mapping <see cref="SyncRuleMapping.Priority"/> ascending (1 = highest), with mapping
    /// id as the deterministic tie-break, regardless of the order the contributions are supplied in. The first
    /// contribution that is connected with a value wins; a connected-but-no-value contribution with "Null is a
    /// value" set asserts null and stops resolution; not-applicable contributions are always skipped; otherwise
    /// resolution falls through. If no contribution has an opinion the result is "no contributor".
    /// </summary>
    /// <param name="contributions">The evaluated contributions to a single Metaverse Object attribute. May be empty.</param>
    /// <returns>The resolution outcome and the winning contribution (if any).</returns>
    public AttributeResolution Resolve(IReadOnlyCollection<AttributeContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);

        // Order defensively rather than trusting the caller: priority ascending (1 = highest), mapping id as the
        // deterministic tie-break. Duplicate priorities within an attribute are prevented by validation, but
        // resolution must still be deterministic if they ever occur.
        var ordered = contributions
            .OrderBy(c => c.Mapping.Priority)
            .ThenBy(c => c.Mapping.Id);

        foreach (var contribution in ordered)
        {
            switch (contribution.State)
            {
                case ContributionState.RuleNotApplicable:
                    // No opinion: always skip to the next priority, regardless of "Null is a value".
                    continue;

                case ContributionState.ConnectedWithValue:
                    return AttributeResolution.Value(contribution);

                case ContributionState.ConnectedNoValue:
                    if (contribution.Mapping.NullIsValue)
                        return AttributeResolution.AssertedNull(contribution);
                    continue; // fall through to the next priority

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(contributions),
                        contribution.State,
                        "Unhandled contribution state during attribute priority resolution.");
            }
        }

        return AttributeResolution.NoContributor();
    }
}
