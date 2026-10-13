// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// What the Object Matching tester (#2055) found for an object holding the sample values: the values the rule would
/// compare, worked out as synchronisation works them out, and optionally what they find in the Metaverse.
/// </summary>
public sealed class ObjectMatchingTestResult
{
    /// <summary>
    /// Why the sample inputs could not be used, one message per problem, such as text that is not a date for a date
    /// and time attribute. When any are present the rule was not evaluated.
    /// </summary>
    public IReadOnlyList<string> InputErrors { get; init; } = [];

    /// <summary>The values the rule would compare; null when <see cref="InputErrors"/> stopped the test.</summary>
    public MatchValueResolution? Resolution { get; init; }

    /// <summary>
    /// What the values find in the Metaverse; null when the lookup was not asked for, or the rule produced no values
    /// to look up.
    /// </summary>
    public ObjectMatchingLookup? Lookup { get; init; }
}
