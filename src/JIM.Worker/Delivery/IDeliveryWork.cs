// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional.DTOs;

namespace JIM.Worker.Delivery;

/// <summary>
/// What a delivery service's loop needs from the rest of JIM, behind an interface so the loop's wake and dispatch
/// logic can be exercised without a database. Each queue has its own implementation, built on a fresh
/// <c>JimApplication</c> per call as the Worker's tasks are: <c>JimPasswordDeliveryWork</c> for the Password
/// Synchronisation queue (#1635), and <c>JimExchangeDeliveryWork</c> for the Exchange queue (#2021).
/// </summary>
internal interface IDeliveryWork
{
    /// <summary>
    /// What the queue holds ahead: due and retrying counts and the earliest scheduled attempt, for the wait and
    /// the heartbeat.
    /// </summary>
    Task<DeliveryQueueOutlook> GetOutlookAsync(DateTime asOf, CancellationToken cancellationToken);

    /// <summary>
    /// The Connected Systems with work a lane would attempt now.
    /// </summary>
    Task<IReadOnlyList<int>> GetConnectedSystemIdsWithWorkDueAsync(DateTime asOf, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one lane: claims and delivers the work due on the lane's Connected System, setting
    /// <see cref="DeliveryLane.ConnectedSystemName"/> once the system is loaded. Delivery outcomes are
    /// recorded on the rows and their Activities; what escapes this method is a fault of the lane as a whole.
    /// </summary>
    /// <returns>
    /// Whether the lane could deliver at all. <see cref="DeliveryLaneOutcome.CouldNotDeliver"/> means it
    /// gave every claim back unattempted (the Connector lacks the capability, or its channel could not be opened),
    /// so the same work is due again the moment it finishes; the scheduler holds the system off for a poll rather
    /// than running the same failing lane back to back.
    /// </returns>
    Task<DeliveryLaneOutcome> RunLaneAsync(DeliveryLane lane, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the service's heartbeat, if one is due. The writer throttles; the loop calls this freely.
    /// </summary>
    Task WriteHeartbeatAsync(string? currentWork, DateTime? currentWorkStartedAt, string? detail, CancellationToken cancellationToken);
}
