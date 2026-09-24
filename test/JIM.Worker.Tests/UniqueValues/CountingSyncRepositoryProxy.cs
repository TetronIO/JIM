// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Reflection;
using JIM.Data.Repositories;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// Wraps an <see cref="ISyncRepository"/> and counts every call made to it, by method name, so a test can
/// assert a gate never queried a method it should have short-circuited past (Unique Value Generation, #242,
/// Phase 2 step 7: "gate order and short-circuit ... use a counting repository wrapper"). Built on
/// <see cref="DispatchProxy"/> rather than a hand-written pass-through, because <see cref="ISyncRepository"/>
/// has well over a hundred members; a dispatch proxy forwards every one of them through a single
/// <see cref="Invoke"/> override without needing to be kept in step with the interface as it grows.
/// </summary>
public class CountingSyncRepositoryProxy : DispatchProxy
{
    private ISyncRepository _inner = null!;
    private readonly ConcurrentDictionary<string, int> _callCounts = new();

    /// <summary>
    /// Creates a counting proxy over <paramref name="inner"/>. The returned repository is what a test hands to
    /// <see cref="JIM.Application.UniqueValues.UniqueValueGenerationServer"/>; <c>Counts</c> reads how many
    /// times each method was called so far, and can be cleared between phases of a test that wants to assert on
    /// calls made after some setup rather than across the whole test.
    /// </summary>
    public static (ISyncRepository Repository, ConcurrentDictionary<string, int> Counts) Create(ISyncRepository inner)
    {
        var proxy = Create<ISyncRepository, CountingSyncRepositoryProxy>();
        var counting = (CountingSyncRepositoryProxy)(object)proxy;
        counting._inner = inner;
        return (proxy, counting._callCounts);
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            throw new InvalidOperationException("DispatchProxy invoked with no target method.");

        _callCounts.AddOrUpdate(targetMethod.Name, 1, static (_, current) => current + 1);
        return targetMethod.Invoke(_inner, args);
    }
}
