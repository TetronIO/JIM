// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// A request to clear one or both of a Connected System Object's marks once the synchronisation that loaded it has
/// processed it: <c>SynchronisationPending</c> (#2092), set by the writers that change what synchronisation must see,
/// and <c>DerivedInputChangePending</c> (#1750), set by another system's synchronisation. The clear applies only while
/// the row still carries <see cref="SeenRowVersion"/>, the PostgreSQL <c>xmin</c> the synchronisation read when it
/// loaded the object: a write committed after that read (an import's change, another run's mark) moves the row
/// version, so the object stays marked and the next run processes what this one never saw.
/// </summary>
/// <param name="ConnectedSystemObjectId">The Connected System Object whose marks are to be cleared.</param>
/// <param name="SeenRowVersion">The row's <c>xmin</c> as the synchronisation loaded it.</param>
/// <param name="SynchronisationPending">Whether to clear the synchronisation-pending mark.</param>
/// <param name="DerivedInputChangePending">Whether to clear the derived-input mark.</param>
public readonly record struct ConnectedSystemObjectMarkClear(
    Guid ConnectedSystemObjectId,
    uint SeenRowVersion,
    bool SynchronisationPending,
    bool DerivedInputChangePending);
