// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Interfaces;

/// <summary>
/// Enables a Connector that reads a Connected System's Object Types one search at a time to read them in the order a
/// synchronisation pages them (#2492): every type after the types its mapped reference attributes can point at, so the
/// objects a reference names have usually been read before the reference itself.
/// </summary>
/// <remarks>
/// <para>
/// Only a Connector that imports using calls (<see cref="IConnectorImportUsingCalls"/>) is handed an order, and only for
/// a Full Import. JIM hands it once per run, before it opens the import connection, as it hands
/// <see cref="IConnectorPartitionWatermarks.SetPartitionWatermarks"/>. The order is a list of groups of Object Type ids,
/// in reading order. Types in one group reference one another (a reference cycle, or a type referencing itself) and can
/// be read together; a type in a later group should not be read before every earlier group has been read.
/// </para>
/// <para>
/// The order is advice about speed, never about correctness: JIM resolves a reference whichever page its target arrives
/// on. A selected Object Type the order does not name is still read, after every type it does name.
/// </para>
/// </remarks>
public interface IConnectorObjectTypeReadOrder
{
    /// <summary>
    /// Hands the Connector the order to read the selected Object Types in. Called at most once per run, before the
    /// import connection is opened.
    /// </summary>
    /// <param name="groups">The Object Type ids in reading order, grouped: the types of one group may be read together,
    /// and each group after the one before it. Read-only, and unchanged for the run.</param>
    void SetObjectTypeReadOrder(IReadOnlyList<IReadOnlyList<int>> groups);
}
