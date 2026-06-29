// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;

namespace JIM.Application.Servers;

/// <summary>
/// Tracks the values an example data template execution has already generated, so the generator can guarantee uniqueness
/// and assign sequential / unique-suffix integers. One instance is created per template execution and threaded through
/// the (parallel) generation loop.
/// <para>
/// This replaces an earlier <c>List&lt;ExampleDataValueTracker&gt;</c> that was scanned with a LINQ <c>SingleOrDefault</c>
/// under a single global lock for every value generated. That was O(n^2) in the number of generated objects (each scan
/// walked the ever-growing list) and serialised the parallel loop on one lock; generating the built-in 10,000-user
/// template spent minutes here. Every lookup below is O(1) and lock-free, keyed by (object type, attribute, value).
/// </para>
/// </summary>
internal sealed class ExampleDataValueTrackerStore
{
    // (objectTypeId, attributeId, baseValue) -> number of times that base value has been requested.
    // Backs the [UniqueInt] system variable.
    private readonly ConcurrentDictionary<(int ObjectTypeId, int AttributeId, string BaseValue), int> _uniqueIntCounters = new();

    // (objectTypeId, attributeId, value) -> presence. Enforces single-use of a fully generated value.
    private readonly ConcurrentDictionary<(int ObjectTypeId, int AttributeId, string Value), byte> _reservedValues = new();

    // (objectTypeId, attributeId) -> last sequential integer assigned.
    private readonly ConcurrentDictionary<(int ObjectTypeId, int AttributeId), int> _sequentialCounters = new();

    /// <summary>
    /// Records another use of <paramref name="baseValue"/> for the given object type and attribute and returns the
    /// occurrence count: 1 for the first use (the caller renders the value with no suffix, as it is unique so far) and
    /// 2, 3, ... for each subsequent use (the caller appends the returned integer to disambiguate). Distinct base
    /// values, attributes, and object types are counted independently.
    /// </summary>
    public int NextUniqueIntSuffix(int objectTypeId, int attributeId, string baseValue) =>
        _uniqueIntCounters.AddOrUpdate((objectTypeId, attributeId, baseValue), 1, static (_, current) => current + 1);

    /// <summary>
    /// Attempts to reserve a fully generated <paramref name="value"/> for the given object type and attribute. Returns
    /// true if the value had not been used before (it is now reserved), or false if it is a duplicate and the caller
    /// should regenerate. The check-and-reserve is atomic, so a value is reserved exactly once even under contention.
    /// </summary>
    public bool TryReserveValue(int objectTypeId, int attributeId, string value) =>
        _reservedValues.TryAdd((objectTypeId, attributeId, value), 0);

    /// <summary>
    /// Returns the next sequential integer for the given object type and attribute. The first call for a given
    /// (object type, attribute) returns <paramref name="seed"/>; each subsequent call returns one more than the last,
    /// regardless of the seed passed thereafter. Distinct attributes and object types are counted independently.
    /// </summary>
    public int NextSequential(int objectTypeId, int attributeId, int seed) =>
        _sequentialCounters.AddOrUpdate((objectTypeId, attributeId), seed, static (_, current) => current + 1);
}
