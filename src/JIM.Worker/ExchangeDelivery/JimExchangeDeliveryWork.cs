// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Transactional.DTOs;
using JIM.Utilities;
using JIM.Worker.Delivery;
using Serilog;

namespace JIM.Worker.ExchangeDelivery;

/// <summary>
/// The Exchange Delivery Service's work against JIM proper (#2021): each call runs on a fresh <c>JimApplication</c>,
/// as Worker tasks do, so no DbContext is shared between the loop, the heartbeat and the lanes running alongside.
/// <para>
/// While the <see cref="FeatureFlagCatalogue.ExchangeRecipients"/> flag is off the service does nothing that can be
/// seen: it reports no work, runs no lane and writes no heartbeat, so it appears nowhere on the Operations page. It
/// looks again on every iteration, so switching the flag on needs no restart.
/// </para>
/// </summary>
internal sealed class JimExchangeDeliveryWork : IDeliveryWork
{
    private readonly IJimApplicationFactory _jimFactory;
    private readonly ServiceHeartbeatWriter _heartbeat;

    public JimExchangeDeliveryWork(IJimApplicationFactory jimFactory, ServiceHeartbeatWriter heartbeat)
    {
        _jimFactory = jimFactory;
        _heartbeat = heartbeat;
    }

    /// <summary>The instance stamped on every change a lane claims.</summary>
    public string InstanceId => _heartbeat.InstanceId;

    public async Task<DeliveryQueueOutlook> GetOutlookAsync(DateTime asOf, CancellationToken cancellationToken)
    {
        using var jim = _jimFactory.Create();
        return await IsSwitchedOnAsync(jim) ? await jim.ExchangeDelivery.GetDeliveryOutlookAsync(asOf) : new DeliveryQueueOutlook();
    }

    public async Task<IReadOnlyList<int>> GetConnectedSystemIdsWithWorkDueAsync(DateTime asOf, CancellationToken cancellationToken)
    {
        using var jim = _jimFactory.Create();
        return await IsSwitchedOnAsync(jim) ? await jim.ExchangeDelivery.GetConnectedSystemIdsWithWorkDueAsync(asOf) : [];
    }

    public async Task<DeliveryLaneOutcome> RunLaneAsync(DeliveryLane lane, CancellationToken cancellationToken)
    {
        using var jim = _jimFactory.Create();

        Log.Information("ExchangeDeliveryService: Delivering queued Exchange changes on Connected System {ConnectedSystemId}.", lane.ConnectedSystemId);

        var result = await jim.ExchangeDelivery.DeliverDueAsync(lane.ConnectedSystemId, InstanceId, DateTime.UtcNow, cancellationToken);
        lane.ConnectedSystemName = result.ConnectedSystemName;

        // The lane's own summary line (counts, session cost and any problem) is logged by the delivery server; this one
        // says which way the scheduler should go next.
        if (result.CouldNotDeliver)
            Log.Information("ExchangeDeliveryService: The lane for {ConnectedSystem} could not deliver; the system is held off until the next poll.",
                LogSanitiser.Sanitise(lane.Describe()));

        return result.CouldNotDeliver ? DeliveryLaneOutcome.CouldNotDeliver : DeliveryLaneOutcome.Completed;
    }

    public async Task WriteHeartbeatAsync(string? currentWork, DateTime? currentWorkStartedAt, string? detail, CancellationToken cancellationToken)
    {
        using var jim = _jimFactory.Create();
        if (await IsSwitchedOnAsync(jim))
            await _heartbeat.WriteAsync(jim, currentWork, currentWorkStartedAt, detail, cancellationToken);
    }

    private static Task<bool> IsSwitchedOnAsync(JimApplication jim) =>
        jim.FeatureFlags.IsEnabledAsync(FeatureFlagCatalogue.ExchangeRecipients.Key);
}
