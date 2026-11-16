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
    private int _failOnCall = 1;
    private int _calls;
    private Action? _onCall;

    /// <summary>
    /// Creates a proxy over <paramref name="inner"/> whose <paramref name="failingMethodName"/> returns a faulted
    /// task carrying <paramref name="exception"/>, as a failing asynchronous call does, instead of calling through.
    /// The method may return <see cref="Task"/> or <see cref="Task{TResult}"/>. With <paramref name="failOnCall"/>, the
    /// calls before that one go through, so a run can fail part-way through a step it repeats (a save batch).
    /// </summary>
    public static ISyncServer Create(ISyncServer inner, string failingMethodName, Exception exception, int failOnCall = 1)
    {
        var proxy = Create<ISyncServer, FaultingSyncServerProxy>();
        var faulting = (FaultingSyncServerProxy)(object)proxy;
        faulting._inner = inner;
        faulting._failingMethodName = failingMethodName;
        faulting._exception = exception;
        faulting._failOnCall = failOnCall;
        return proxy;
    }

    /// <summary>
    /// Creates a proxy over <paramref name="inner"/> that runs <paramref name="onCall"/> as <paramref name="methodName"/>
    /// is called, then calls through: a run can be cancelled while one of its steps is under way.
    /// </summary>
    public static ISyncServer CreateObserving(ISyncServer inner, string methodName, Action onCall)
    {
        var proxy = Create<ISyncServer, FaultingSyncServerProxy>();
        var observing = (FaultingSyncServerProxy)(object)proxy;
        observing._inner = inner;
        observing._failingMethodName = methodName;
        observing._onCall = onCall;
        return proxy;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null)
            throw new InvalidOperationException("DispatchProxy invoked with no target method.");

        if (targetMethod.Name != _failingMethodName || ++_calls < _failOnCall)
            return targetMethod.Invoke(_inner, args);

        if (_onCall != null)
        {
            _onCall();
            return targetMethod.Invoke(_inner, args);
        }

        if (targetMethod.ReturnType == typeof(Task))
            return Task.FromException(_exception);

        if (targetMethod.ReturnType.IsGenericType && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            return FaultedTaskOf(targetMethod.ReturnType.GetGenericArguments()[0]);

        throw new NotSupportedException($"FaultingSyncServerProxy only fails methods returning Task or Task<T>; {targetMethod.Name} returns {targetMethod.ReturnType.Name}.");
    }

    /// <summary>
    /// A <see cref="Task{TResult}"/> of <paramref name="resultType"/> faulted with the proxy's exception.
    /// </summary>
    private object FaultedTaskOf(Type resultType) =>
        typeof(Task).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Task.FromException) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(resultType)
            .Invoke(null, [_exception])!;
}
