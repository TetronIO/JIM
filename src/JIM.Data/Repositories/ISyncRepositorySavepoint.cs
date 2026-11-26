// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Data.Repositories;

/// <summary>
/// A point inside the open transaction that a sync operation can roll back to without losing the rest of it (#2848): a
/// synchronisation page writes its state in one transaction, and a step that tries a set-based write and falls back to
/// one object at a time must undo the failed attempt first, since in PostgreSQL a failed statement aborts everything
/// after it until the transaction rolls back to a savepoint.
/// </summary>
public interface ISyncRepositorySavepoint
{
    /// <summary>
    /// Undoes every write made through the repository since the savepoint, leaving the transaction usable. The join
    /// state of every Connected System Object the repository tracks (its Metaverse Object and its join record) is put
    /// back as it stood when the savepoint was created, since the raw-SQL writes that change joins fix tracked objects up
    /// to match, and the rollback undoes only the database's half of that.
    /// </summary>
    Task RollbackAsync();

    /// <summary>
    /// Keeps every write made since the savepoint and discards the savepoint.
    /// </summary>
    Task ReleaseAsync();
}
