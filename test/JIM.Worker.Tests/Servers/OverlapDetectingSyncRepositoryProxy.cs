// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Data.Repositories;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Wraps an <see cref="ISyncRepository"/> and counts the calls made to it while something else that shares the same
/// database context is mid-write, as reported by <c>isOtherWriteInFlight</c>. On the Worker every repository of one
/// task shares one Entity Framework context, which throws if two operations overlap; the in-memory repository has no
/// such check, so a test uses this to see the overlap the real context would refuse.
/// </summary>
public class OverlapDetectingSyncRepositoryProxy : DispatchProxy
{
    private ISyncRepository _inner = null!;
    private Func<bool> _isOtherWriteInFlight = null!;
    private int[] _overlaps = null!;

    /// <summary>
    /// Creates the proxy. <c>Overlaps[0]</c> counts the calls made while <paramref name="isOtherWriteInFlight"/> was true.
    /// </summary>
    public static (ISyncRepository Repository, int[] Overlaps) Create(ISyncRepository inner, Func<bool> isOtherWriteInFlight)
    {
        var proxy = Create<ISyncRepository, OverlapDetectingSyncRepositoryProxy>();
        var detecting = (OverlapDetectingSyncRepositoryProxy)(object)proxy;
        detecting._inner = inner;
        detecting._isOtherWriteInFlight = isOtherWriteInFlight;
        detecting._overlaps = new int[1];
        return (proxy, detecting._overlaps);
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            throw new InvalidOperationException("DispatchProxy invoked with no target method.");

        if (_isOtherWriteInFlight())
            Interlocked.Increment(ref _overlaps[0]);

        return targetMethod.Invoke(_inner, args);
    }
}
