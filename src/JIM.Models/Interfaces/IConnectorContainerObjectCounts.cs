// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;

namespace JIM.Models.Interfaces;

/// <summary>
/// Enables a Connector to report how many objects each of its Containers holds, read live from the Connected
/// System.
/// </summary>
/// <remarks>
/// Deselecting a Container is silently destructive, and the number that most directly informs the decision is how
/// many objects are in it. JIM cannot work that out for itself: it could count the Connected System Objects it
/// already holds, but an administrator choosing Containers is usually doing so while setting up a Connected System
/// that has never imported anything, where such a figure reads zero for every Container. Only the Connected System
/// knows, so the Connector is asked.
///
/// Implement this alongside <see cref="IConnectorPartitions"/>, whose shape it follows: settings in, one partition
/// at a time, live data out. JIM calls it as part of retrieving the hierarchy, so a Connector that implements it is
/// answering at the same moment it reports its Containers, against the same directory state.
///
/// Three rules matter:
///
/// <list type="bullet">
/// <item>
/// <b>Count what an import would return.</b> Apply the same filter a Full Import builds for the Object Types named
/// in <c>objectTypeNames</c>, so the figure and the next import agree. A count that included objects the import
/// will never bring back overstates what deselecting a Container costs.
/// </item>
/// <item>
/// <b>Count objects, not Containers.</b> Report the objects sitting directly in each Container. JIM rolls those up
/// into subtree totals itself, so a Container's own scope setting is none of the Connector's business here.
/// </item>
/// <item>
/// <b>Say when the answer is partial.</b> A server-imposed size or time limit, or a cancellation, means the counts
/// are lower than the truth. Report that through <see cref="ConnectorContainerObjectCountResult.Complete"/> rather
/// than returning the short numbers as though they were whole; JIM would otherwise tell an administrator a
/// destructive change is cheaper than it is.
/// </item>
/// </list>
///
/// Not implementing it is a valid answer. The Partitions and Containers tab simply shows no counts, which is
/// honest, rather than showing zeroes that read as "every Container is empty".
/// </remarks>
public interface IConnectorContainerObjectCounts
{
    /// <summary>
    /// Counts the objects held by each Container in one partition.
    /// </summary>
    /// <param name="settingValues">The Connected System's settings, from which the Connector opens its connection.</param>
    /// <param name="connectorPartition">
    /// The partition to count, as returned by <see cref="IConnectorPartitions.GetPartitionsAsync"/>.
    /// </param>
    /// <param name="objectTypeNames">
    /// The Object Types the administrator has selected to manage. Empty means none have been selected yet, in which
    /// case the Connector should count nothing rather than counting everything: a total across object types JIM will
    /// never import is not a number anyone can act on.
    /// </param>
    /// <param name="logger">The logger to report progress and problems through.</param>
    /// <param name="cancellationToken">
    /// Cancels a search that is taking longer than an administrator is willing to wait. A cancelled count returns
    /// what it has with <see cref="ConnectorContainerObjectCountResult.Complete"/> false, rather than throwing:
    /// partial counts clearly labelled are more use than none.
    /// </param>
    Task<ConnectorContainerObjectCountResult> GetContainerObjectCountsAsync(
        List<ConnectedSystemSettingValue> settingValues,
        ConnectorPartition connectorPartition,
        IReadOnlyList<string> objectTypeNames,
        ILogger logger,
        CancellationToken cancellationToken);
}
