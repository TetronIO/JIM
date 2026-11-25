// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// What the Metaverse Objects holding an attribute's values look like today, counted once, from which the impact of any
/// change of contribution mode is read (#3059). A contributor is a Synchronisation Rule that is enabled and whose
/// Connected System has an object of the rule's type joined to the Metaverse Object (and not obsolete).
/// </summary>
/// <param name="ValuesNoLongerSupplied">Values whose recorded contributor is no longer a contributor: its rule deleted
/// or disabled, or no object of its system joined. Only Accumulate keeps such values.</param>
/// <param name="ObjectsWithValuesNoLongerSupplied">Metaverse Objects holding one or more such values.</param>
/// <param name="ObjectsWithSeveralContributors">Metaverse Objects holding values from two or more contributors, which
/// Priority narrows to the one contributor with the highest priority.</param>
/// <param name="ObjectsLosingValuesUnderPriority">Metaverse Objects in either of the two counts above.</param>
/// <param name="ObjectsThatMayGainValues">Metaverse Objects with two or more contributors joined but holding values from
/// fewer of them, which Accumulate or Merge would add the others' values to.</param>
public sealed record ContributionModeImpactCounts(
    int ValuesNoLongerSupplied,
    int ObjectsWithValuesNoLongerSupplied,
    int ObjectsWithSeveralContributors,
    int ObjectsLosingValuesUnderPriority,
    int ObjectsThatMayGainValues);
