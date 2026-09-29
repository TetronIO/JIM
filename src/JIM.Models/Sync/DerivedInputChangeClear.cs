// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// A request to clear the Metaverse-Derived Attribute Flow mark (#1750) on one Connected System Object that its
/// hosting system's synchronisation re-evaluated without error. The clear applies only while the row still carries
/// <see cref="SeenRowVersion"/>, the PostgreSQL <c>xmin</c> the synchronisation read when it loaded the object: a
/// mark set by another run after that read moves the row version, so the object stays marked and is re-evaluated
/// next run (fail-safe) rather than losing an input change it never saw.
/// </summary>
/// <param name="ConnectedSystemObjectId">The Connected System Object whose mark is to be cleared.</param>
/// <param name="SeenRowVersion">The row's <c>xmin</c> as the synchronisation loaded it.</param>
public readonly record struct DerivedInputChangeClear(Guid ConnectedSystemObjectId, uint SeenRowVersion);
