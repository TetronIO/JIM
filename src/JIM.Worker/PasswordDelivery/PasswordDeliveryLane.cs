// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.PasswordDelivery;

/// <summary>
/// One delivery lane in flight: the Password Delivery Service delivering to one Connected System (#1635). The
/// scheduler creates one per system it dispatches, the work fills in the system's name once it has loaded it, and
/// the heartbeat describes the lanes in flight as the service's current work.
/// </summary>
internal sealed class PasswordDeliveryLane
{
    public PasswordDeliveryLane(int connectedSystemId, DateTime startedAt)
    {
        ConnectedSystemId = connectedSystemId;
        StartedAt = startedAt;
    }

    public int ConnectedSystemId { get; }

    /// <summary>
    /// When the lane was dispatched (UTC). The earliest across the lanes in flight is the heartbeat's
    /// CurrentWorkStartedAt.
    /// </summary>
    public DateTime StartedAt { get; }

    /// <summary>
    /// The Connected System's name, set by the work once it has loaded the system; null until then, in which case
    /// the lane is described by the system's id.
    /// </summary>
    public string? ConnectedSystemName { get; set; }

    /// <summary>
    /// The lane in an administrator's words, for the heartbeat: the system's name where known.
    /// </summary>
    public string Describe() => ConnectedSystemName ?? $"Connected System {ConnectedSystemId}";
}
