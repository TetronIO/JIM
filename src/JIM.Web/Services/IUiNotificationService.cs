// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Tasking;

namespace JIM.Web.Services;

/// <summary>
/// In-process relay for real-time database notifications (issue #307). Blazor Server components subscribe
/// to these events instead of a SignalR client; the singleton <c>NotificationListenerService</c> raises
/// them as PostgreSQL NOTIFY events arrive. Notifications are hints, not data: subscribers must re-query
/// via the application layer, and every consumer retains a polling fallback (use
/// <see cref="IsRealTimeAvailable"/> to pick the polling interval) for anything missed while disconnected.
/// </summary>
public interface IUiNotificationService
{
    /// <summary>
    /// Raised when a Worker Task is inserted, changes status, or is deleted (deletion signals terminal
    /// completion or cancellation).
    /// </summary>
    event Action<WorkerTaskChangeNotification>? WorkerTaskChanged;

    /// <summary>
    /// Raised with the Activity id when an Activity's progress or status changes. Bursts are coalesced,
    /// so subscribers receive at most one event per Activity per debounce window.
    /// </summary>
    event Action<Guid>? ActivityProgressChanged;

    /// <summary>
    /// Whether the database notification listener currently holds a connection, meaning real-time events
    /// are flowing. Consumers should poll slowly (as a reconciliation safety net) when true and fall back
    /// to fast polling when false.
    /// </summary>
    bool IsRealTimeAvailable { get; }

    /// <summary>
    /// Raised when <see cref="IsRealTimeAvailable"/> changes. Subscribers typically trigger an immediate
    /// refresh on reconnection to reconcile anything missed while real-time updates were unavailable.
    /// </summary>
    event Action<bool>? RealTimeAvailabilityChanged;
}
