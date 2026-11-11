// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using JIM.Data.Repositories;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// An <see cref="ISyncRepository"/> that hands every call to the repository it wraps and records it in order: the method's
/// name and, for each argument that is a collection, how many items it held when the call was made. Lets a test say what a
/// processor asked of the database, and in what order, while the database still answers.
/// </summary>
public class RecordingSyncRepository : DispatchProxy
{
    private ISyncRepository _inner = null!;

    /// <summary>
    /// Every call made, in order.
    /// </summary>
    public ConcurrentQueue<RecordedSyncRepositoryCall> Calls { get; } = new();

    /// <summary>
    /// Called with each call's method name and arguments before the call is handed on, so a test can act part-way
    /// through a run, as an administrator cancelling it does.
    /// </summary>
    public Action<string, object?[]>? BeforeCall { get; set; }

    /// <summary>
    /// Given each call's method name and arguments, the exception to throw instead of handing the call on, or null to hand
    /// it on: a database failure at a chosen point of a run.
    /// </summary>
    public Func<string, object?[], Exception?>? FailWhen { get; set; }

    /// <summary>
    /// Wraps <paramref name="inner"/>, returning the wrapper as the repository and its recorder.
    /// </summary>
    public static (ISyncRepository Repository, RecordingSyncRepository Recorder) Wrap(ISyncRepository inner)
    {
        var repository = Create<ISyncRepository, RecordingSyncRepository>();
        var recorder = (RecordingSyncRepository)(object)repository;
        recorder._inner = inner;
        return (repository, recorder);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        Calls.Enqueue(new RecordedSyncRepositoryCall(targetMethod.Name,
            (args ?? []).Select(arg => arg is ICollection collection ? collection.Count : (int?)null).ToList(),
            (args ?? []).Select(arg => arg is IEnumerable<Guid> ids ? ids.ToList() : null).ToList()));

        BeforeCall?.Invoke(targetMethod.Name, args ?? []);
        if (FailWhen?.Invoke(targetMethod.Name, args ?? []) is { } failure)
            throw failure;

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
