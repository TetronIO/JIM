// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Application.Interfaces;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Wraps an <see cref="ISyncServer"/> and fails one named method with a given exception, forwarding every other
/// call to the real server, so a test can make one step of a run fail after the steps before it have genuinely
/// happened (#1868: staging failing after every import page has been read). Built on <see cref="DispatchProxy"/>
/// for the same reason as <c>CountingSyncRepositoryProxy</c>: the interface is large, and a single
/// <see cref="Invoke"/> override stays correct as it grows.
/// </summary>
public class FaultingSyncServerProxy : DispatchProxy
{
    private ISyncServer _inner = null!;
    private string _failingMethodName = null!;
    private Exception _exception = null!;

    /// <summary>
    /// Creates a proxy over <paramref name="inner"/> whose <paramref name="failingMethodName"/> returns a faulted
    /// task carrying <paramref name="exception"/>, as a failing asynchronous call does, instead of calling through.
    /// </summary>
    public static ISyncServer Create(ISyncServer inner, string failingMethodName, Exception exception)
    {
        var proxy = Create<ISyncServer, FaultingSyncServerProxy>();
        var faulting = (FaultingSyncServerProxy)(object)proxy;
        faulting._inner = inner;
        faulting._failingMethodName = failingMethodName;
        faulting._exception = exception;
        return proxy;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            throw new InvalidOperationException("DispatchProxy invoked with no target method.");

        if (targetMethod.Name != _failingMethodName)
            return targetMethod.Invoke(_inner, args);

        if (targetMethod.ReturnType != typeof(Task))
            throw new NotSupportedException($"FaultingSyncServerProxy only fails methods returning Task; {targetMethod.Name} returns {targetMethod.ReturnType.Name}.");

        return Task.FromException(_exception);
    }
}
