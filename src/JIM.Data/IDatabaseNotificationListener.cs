// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Data;

/// <summary>
/// A long-running listener for database-published notifications (PostgreSQL LISTEN/NOTIFY; issue #307).
/// Implementations maintain a dedicated database connection, invoke a handler for each received
/// notification, and reconnect automatically with backoff when the connection drops. Notifications are
/// fire-and-forget hints; consumers must treat the database as the source of truth and retain a polling
/// fallback for anything missed while disconnected.
/// </summary>
public interface IDatabaseNotificationListener
{
    /// <summary>
    /// Whether the listener currently holds an open connection and is receiving notifications.
    /// Consumers can use this to decide between event-driven behaviour and their polling fallback.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Raised when the listener connects (true) or loses its connection (false).
    /// </summary>
    event Action<bool>? ConnectionStateChanged;

    /// <summary>
    /// Listens on the supplied channels until the cancellation token is cancelled, invoking
    /// <paramref name="onNotificationAsync"/> with the channel name and payload for each notification
    /// received. Reconnects automatically on connection failure; returns normally on cancellation.
    /// </summary>
    Task ListenAsync(
        IReadOnlyCollection<string> channelNames,
        Func<string, string, CancellationToken, Task> onNotificationAsync,
        CancellationToken cancellationToken);
}
