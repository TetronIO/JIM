// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// The result of resolving a Metaverse Object attribute across its contributing import Synchronisation Rules (#91).
/// Carries the outcome and, for a winning value or asserted null, the contribution that won (so the engine can
/// source the value and stamp provenance: <c>ContributedBySyncRuleId</c> and <c>ContributedBySystemId</c>).
/// </summary>
public class AttributeResolution
{
    /// <summary>
    /// The resolution outcome.
    /// </summary>
    public required AttributeResolutionOutcome Outcome { get; init; }

    /// <summary>
    /// The contribution that won resolution. Set for <see cref="AttributeResolutionOutcome.Value"/> and
    /// <see cref="AttributeResolutionOutcome.AssertedNull"/>; null for <see cref="AttributeResolutionOutcome.NoContributor"/>.
    /// </summary>
    public AttributeContribution? WinningContribution { get; init; }

    /// <summary>
    /// Convenience accessor for the winning mapping (null when there was no contributor).
    /// </summary>
    public SyncRuleMapping? WinningMapping => WinningContribution?.Mapping;

    /// <summary>
    /// A value contribution won.
    /// </summary>
    public static AttributeResolution Value(AttributeContribution winning) =>
        new() { Outcome = AttributeResolutionOutcome.Value, WinningContribution = winning };

    /// <summary>
    /// A connected, in-scope contribution with "Null is a value" set asserted no value.
    /// </summary>
    public static AttributeResolution AssertedNull(AttributeContribution winning) =>
        new() { Outcome = AttributeResolutionOutcome.AssertedNull, WinningContribution = winning };

    /// <summary>
    /// No contribution had an opinion.
    /// </summary>
    public static AttributeResolution NoContributor() =>
        new() { Outcome = AttributeResolutionOutcome.NoContributor };
}
