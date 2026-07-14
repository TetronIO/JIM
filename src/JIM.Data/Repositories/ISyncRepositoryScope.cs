// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Data.Repositories;

/// <summary>
/// A disposable ownership scope around an <see cref="ISyncRepository"/> created for a bounded unit
/// of work, such as one parallel export batch. Disposing the scope releases whatever owns the
/// repository's DbContext (for example a per-batch JimApplication), returning its database
/// connection to the pool. The parallel export path creates one scope per batch; before scopes
/// existed, each batch's context lived until process exit and pinned a pooled connection
/// (the Scale200k10kGroups export pool exhaustion of 2026-07-13).
/// </summary>
public interface ISyncRepositoryScope : IDisposable
{
    /// <summary>
    /// The repository to use for the scope's unit of work. Not valid after the scope is disposed.
    /// </summary>
    ISyncRepository Repository { get; }
}

/// <summary>
/// Standard <see cref="ISyncRepositoryScope"/> implementation: pairs a repository with the
/// disposable that owns its context. Pass a null owner when there is nothing to release
/// (for example in-memory test repositories).
/// </summary>
public sealed class SyncRepositoryScope(ISyncRepository repository, IDisposable? owner = null) : ISyncRepositoryScope
{
    public ISyncRepository Repository { get; } = repository;

    public void Dispose() => owner?.Dispose();
}
