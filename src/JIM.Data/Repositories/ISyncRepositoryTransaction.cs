// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Data.Repositories;

/// <summary>
/// A database transaction a sync operation holds over several repository writes that must land together or not at all
/// (#2197). Disposing it without <see cref="CommitAsync"/> rolls every write back.
/// </summary>
public interface ISyncRepositoryTransaction : IAsyncDisposable
{
    /// <summary>
    /// Commits every write made through the repository since the transaction began.
    /// </summary>
    Task CommitAsync();
}
