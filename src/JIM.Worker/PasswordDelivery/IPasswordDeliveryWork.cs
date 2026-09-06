// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional.DTOs;

namespace JIM.Worker.PasswordDelivery;

/// <summary>
/// What the Password Delivery Service's loop needs from the rest of JIM (#1635), behind an interface so the loop's
/// wake and dispatch logic can be exercised without a database: <see cref="JimPasswordDeliveryWork"/> is the real
/// implementation, built on a fresh <c>JimApplication</c> per call as the Worker's tasks are.
/// </summary>
internal interface IPasswordDeliveryWork
{
    /// <summary>
    /// What the queue holds ahead: due and retrying counts and the earliest scheduled attempt, for the wait and
    /// the heartbeat.
    /// </summary>
    Task<PasswordQueueDeliveryOutlook> GetOutlookAsync(DateTime asOf, CancellationToken cancellationToken);

    /// <summary>
    /// The Connected Systems with work a lane would attempt now.
    /// </summary>
    Task<IReadOnlyList<int>> GetConnectedSystemIdsWithWorkDueAsync(DateTime asOf, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one lane: claims and delivers the work due on the lane's Connected System, setting
    /// <see cref="PasswordDeliveryLane.ConnectedSystemName"/> once the system is loaded. Delivery outcomes are
    /// recorded on the rows and their Activities; what escapes this method is a fault of the lane as a whole.
    /// </summary>
    /// <returns>
    /// Whether the lane could deliver at all. <see cref="PasswordDeliveryLaneOutcome.CouldNotDeliver"/> means it
    /// gave every claim back unattempted (no password capability, channel could not be opened or was refused),
    /// so the same work is due again the moment it finishes; the scheduler holds the system off for a poll rather
    /// than running the same failing lane back to back.
    /// </returns>
    Task<PasswordDeliveryLaneOutcome> RunLaneAsync(PasswordDeliveryLane lane, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the service's heartbeat, if one is due. The writer throttles; the loop calls this freely.
    /// </summary>
    Task WriteHeartbeatAsync(string? currentWork, DateTime? currentWorkStartedAt, string? detail, CancellationToken cancellationToken);
}
