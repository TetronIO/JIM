// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// What a saved configuration test case expects (#2055 Phase 4).
/// </summary>
/// <param name="Kind">The outcome expected.</param>
/// <param name="Values">For <see cref="ConfigurationTestExpectationKind.Values"/>, the values expected, as text in
/// their culture-invariant form; empty otherwise.</param>
/// <param name="InOrder">For <see cref="ConfigurationTestExpectationKind.Values"/>, whether the values must come in
/// this order. By default they compare as an unordered set of values, repeats counted.</param>
public sealed record ConfigurationTestExpectation(
    ConfigurationTestExpectationKind Kind,
    IReadOnlyList<string>? Values = null,
    bool InOrder = false)
{
    /// <summary>
    /// The values expected; empty unless <see cref="Kind"/> is <see cref="ConfigurationTestExpectationKind.Values"/>.
    /// </summary>
    public IReadOnlyList<string> Values { get; init; } = Values ?? [];

    /// <summary>
    /// Equal when they expect the same outcome, the same values in the same stated order, and the same order rule.
    /// </summary>
    public bool Equals(ConfigurationTestExpectation? other) =>
        other is not null && Kind == other.Kind && InOrder == other.InOrder && Values.SequenceEqual(other.Values, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(InOrder);
        foreach (var value in Values)
            hash.Add(value, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
