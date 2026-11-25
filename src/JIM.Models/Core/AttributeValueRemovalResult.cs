// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

namespace JIM.Models.Core;

/// <summary>
/// The answer to a request to remove values from an Accumulate attribute by hand (#3059). A removal is all or nothing:
/// either every named value was removed, or none was and <see cref="Refusal"/> says why.
/// </summary>
public sealed class AttributeValueRemovalResult
{
    /// <summary>What became of the request.</summary>
    public AttributeValueRemovalOutcome Outcome { get; init; }

    /// <summary>The values removed; empty when refused.</summary>
    public IReadOnlyList<MetaverseObjectAttributeValue> RemovedValues { get; init; } = [];

    /// <summary>For <see cref="AttributeValueRemovalOutcome.SuppliedByContributor"/>, each named value a rule still supplies.</summary>
    public IReadOnlyList<AccumulatedValueSupplier> Suppliers { get; init; } = [];

    /// <summary>Why nothing was removed; null when removed.</summary>
    public JimMessage? Refusal { get; init; }

    /// <summary>The Activity recording the removal; null when refused.</summary>
    public Guid? ActivityId { get; init; }

    /// <summary>A refusal, with nothing removed.</summary>
    public static AttributeValueRemovalResult Refused(AttributeValueRemovalOutcome outcome, JimMessage refusal,
        IReadOnlyList<AccumulatedValueSupplier>? suppliers = null) =>
        new() { Outcome = outcome, Refusal = refusal, Suppliers = suppliers ?? [] };
}
