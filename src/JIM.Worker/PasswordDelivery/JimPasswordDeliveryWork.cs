// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Models.Transactional.DTOs;
using JIM.Utilities;
using Serilog;

namespace JIM.Worker.PasswordDelivery;

/// <summary>
/// The Password Delivery Service's work against JIM proper (#1635): each call runs on a fresh <c>JimApplication</c>,
/// as Worker tasks do, so no DbContext is shared between the loop, the heartbeat and the lanes running alongside.
/// </summary>
internal sealed class JimPasswordDeliveryWork : IPasswordDeliveryWork
{
    private readonly IJimApplicationFactory _jimFactory;
    private readonly ServiceHeartbeatWriter _heartbeat;

    public JimPasswordDeliveryWork(IJimApplicationFactory jimFactory, ServiceHeartbeatWriter heartbeat)
    {
        _jimFactory = jimFactory;
        _heartbeat = heartbeat;
    }

    /// <summary>
    /// The instance stamped on every row a lane claims.
    /// </summary>
    public string InstanceId => _heartbeat.InstanceId;

    public async Task<PasswordQueueDeliveryOutlook> GetOutlookAsync(DateTime asOf, CancellationToken cancellationToken)
    {
        using var jim = _jimFactory.Create();
        return await jim.PasswordSynchronisation.GetDeliveryOutlookAsync(asOf);
    }

    public async Task<IReadOnlyList<int>> GetConnectedSystemIdsWithWorkDueAsync(DateTime asOf, CancellationToken cancellationToken)
    {
        using var jim = _jimFactory.Create();
        return await jim.PasswordSynchronisation.GetConnectedSystemIdsWithWorkDueAsync(asOf);
    }

    public async Task<PasswordDeliveryLaneOutcome> RunLaneAsync(PasswordDeliveryLane lane, CancellationToken cancellationToken)
    {
        using var jim = _jimFactory.Create();

        // Named before the pass so the heartbeat can say where the service is delivering while it is still
        // binding to the directory. A header read: the pass loads the full system itself.
        var header = await jim.ConnectedSystems.GetConnectedSystemHeaderAsync(lane.ConnectedSystemId);
        lane.ConnectedSystemName = header?.Name;

        Log.Information("PasswordDeliveryService: Delivering queued password changes to {ConnectedSystem}.",
            LogSanitiser.Sanitise(lane.Describe()));

        var result = await jim.PasswordSynchronisation.DeliverDueAsync(lane.ConnectedSystemId, InstanceId, DateTime.UtcNow, cancellationToken);

        // Synchronisation Integrity: summary statistics at the end of every lane. Problems are named so an
        // administrator reading the log knows where to look; the per-system pass has already logged its own
        // counts, and this is the lane's line.
        Log.Information("PasswordDeliveryService: Lane for {ConnectedSystem} finished: {Delivered} delivered, {Retrying} retrying, {Parked} parked, {Expired} expired, {Problems} problem(s).{ProblemDetail}",
            LogSanitiser.Sanitise(lane.Describe()), result.DeliveredCount, result.RetryingCount, result.ParkedCount, result.ExpiredCount,
            result.Problems.Count, result.Problems.Count == 0 ? string.Empty : " " + LogSanitiser.Sanitise(string.Join(" ", result.Problems)));

        // A problem is a lane that could not deliver at all (its claims went back unattempted); the pass reports
        // nothing for a lane that attempted and failed, because those rows are retrying on their own clock.
        return result.Problems.Count > 0 ? PasswordDeliveryLaneOutcome.CouldNotDeliver : PasswordDeliveryLaneOutcome.Completed;
    }

    public async Task WriteHeartbeatAsync(string? currentWork, DateTime? currentWorkStartedAt, string? detail, CancellationToken cancellationToken)
    {
        using var jim = _jimFactory.Create();
        await _heartbeat.WriteAsync(jim, currentWork, currentWorkStartedAt, detail, cancellationToken);
    }
}
