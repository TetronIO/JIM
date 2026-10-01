// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Application.Services;

/// <summary>
/// Decides which Connected Systems must re-evaluate a Metaverse Object after some of its attributes changed
/// (Metaverse-Derived Attribute Flows, #1750, "Position 2"). A derived flow runs only in its hosting system's own
/// synchronisation, so when anything else changes one of its inputs, that hosting system's joined Connected System
/// Object is marked (<c>DerivedInputChangePending</c>) and its next synchronisation, delta included, picks it up.
/// <para>
/// Pure and shared: the worker calls it after each object's change capture and applies the marks in one bulk update
/// per page; the writers outside synchronisation (plan Phase 4) call it through <see cref="DerivedInputMarkBatch"/>, which
/// excludes no system and applies the marks in one bulk update per batch.
/// </para>
/// </summary>
public static class DerivedInputMarking
{
    /// <summary>
    /// The Connected Systems whose import Synchronisation Rules host a derived flow reading any of
    /// <paramref name="changedAttributeIds"/> (transitively through derived levels), excluding
    /// <paramref name="excludedConnectedSystemId"/>: the system whose synchronisation made the change, whose derived
    /// pass has already run on this pass's values. Ascending and distinct; empty when there is no graph (the feature is
    /// off) or nothing derived reads the changed attributes.
    /// </summary>
    /// <param name="graph">The run's derived flow graph, or null when the feature is off.</param>
    /// <param name="metaverseObjectTypeId">The changed Metaverse Object's type.</param>
    /// <param name="changedAttributeIds">The Metaverse attributes whose values changed: additions and removals staged
    /// this pass, which include generated values and re-elected survivors. A provenance-only takeover stages neither,
    /// so it is excluded by construction.</param>
    /// <param name="excludedConnectedSystemId">The Connected System being synchronised, or null for a write outside
    /// synchronisation, which excludes nothing.</param>
    public static IReadOnlyList<int> GetConnectedSystemsToMark(
        DerivedFlowGraph? graph,
        int metaverseObjectTypeId,
        IEnumerable<int> changedAttributeIds,
        int? excludedConnectedSystemId)
    {
        ArgumentNullException.ThrowIfNull(changedAttributeIds);

        if (graph == null)
            return [];

        return changedAttributeIds
            .Distinct()
            .SelectMany(attributeId => graph.GetHostingSystemsReading(metaverseObjectTypeId, attributeId))
            .Where(connectedSystemId => connectedSystemId != excludedConnectedSystemId)
            .Distinct()
            .Order()
            .ToList();
    }

    /// <summary>
    /// <see cref="GetConnectedSystemsToMark"/> for staged attribute value changes: the attribute of each value (by id,
    /// or by its attribute navigation for a value built this pass that carries no id yet).
    /// </summary>
    public static IReadOnlyList<int> GetConnectedSystemsToMark(
        DerivedFlowGraph? graph,
        int metaverseObjectTypeId,
        IEnumerable<MetaverseObjectAttributeValue> changedValues,
        int? excludedConnectedSystemId)
    {
        ArgumentNullException.ThrowIfNull(changedValues);

        if (graph == null)
            return [];

        var attributeIds = changedValues
            .Select(value => value.AttributeId != 0 ? value.AttributeId : value.Attribute?.Id ?? 0)
            .Where(attributeId => attributeId != 0);

        return GetConnectedSystemsToMark(graph, metaverseObjectTypeId, attributeIds, excludedConnectedSystemId);
    }
}
