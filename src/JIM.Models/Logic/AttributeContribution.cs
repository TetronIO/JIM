// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// A single evaluated contribution to a Metaverse Object attribute during attribute priority resolution (#91):
/// one import <see cref="SyncRuleMapping"/> paired with the tri-state outcome of evaluating it against the
/// Metaverse Object (whether its Connected System is connected and in scope, and whether it yielded a value).
/// The engine evaluates each contributing mapping into one of these; the resolver then selects the winner by
/// priority. This type intentionally carries no attribute value payload: the resolver decides purely on
/// <see cref="State"/>, the mapping's "Null is a value" flag, and priority order, and the engine pairs the
/// winning mapping back to the value it already computed.
/// </summary>
public class AttributeContribution
{
    /// <summary>
    /// The import mapping that produced this contribution. Supplies the priority, "Null is a value" flag, and
    /// provenance (the owning Synchronisation Rule and its Connected System).
    /// </summary>
    public required SyncRuleMapping Mapping { get; init; }

    /// <summary>
    /// The evaluated tri-state of this mapping's contribution to the attribute for the Metaverse Object being resolved.
    /// </summary>
    public required ContributionState State { get; init; }
}
