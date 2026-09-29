// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Data.Repositories;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Wraps an <see cref="ISyncRepository"/> and runs a test-supplied action immediately before one named method is
/// forwarded, so a test can make something happen at an exact point in a synchronisation run: for example another
/// system's run re-marking an object between the hosting run's page load and its page-flush clear (#1750). Built on
/// <see cref="DispatchProxy"/> for the reason <see cref="UniqueValues.CountingSyncRepositoryProxy"/> gives.
/// </summary>
public class InterceptingSyncRepositoryProxy : DispatchProxy
{
    private ISyncRepository _inner = null!;
    private string _methodName = null!;
    private Action _before = null!;

    /// <summary>
    /// Creates a proxy over <paramref name="inner"/> that runs <paramref name="before"/> each time
    /// <paramref name="methodName"/> is called, then forwards the call unchanged.
    /// </summary>
    public static ISyncRepository Create(ISyncRepository inner, string methodName, Action before)
    {
        var proxy = Create<ISyncRepository, InterceptingSyncRepositoryProxy>();
        var intercepting = (InterceptingSyncRepositoryProxy)(object)proxy;
        intercepting._inner = inner;
        intercepting._methodName = methodName;
        intercepting._before = before;
        return proxy;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            throw new InvalidOperationException("DispatchProxy invoked with no target method.");

        if (targetMethod.Name == _methodName)
            _before();

        return targetMethod.Invoke(_inner, args);
    }
}
