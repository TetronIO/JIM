// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Serilog;

namespace JIM.Web.Services;

/// <summary>
/// Coalesces bursts of notifications into a single callback (issue #307). <see cref="Notify"/> collects
/// keys; the window opens on the first notification after a flush and, when it elapses, the callback is
/// invoked once with the distinct keys collected. Opening the window on the first notification (rather
/// than resetting it on every notification) guarantees a sustained notification stream still flushes once
/// per window instead of being deferred indefinitely. Thread-safe; disposal stops all further callbacks.
/// </summary>
/// <typeparam name="TKey">The notification key type, for example an Activity id.</typeparam>
public sealed class NotificationDebouncer<TKey> : IDisposable where TKey : notnull
{
    private static readonly TimeSpan DefaultQuietWindow = TimeSpan.FromMilliseconds(200);

    private readonly Lock _lock = new();
    private readonly HashSet<TKey> _pendingKeys = [];
    private readonly Action<IReadOnlyCollection<TKey>> _onFlush;
    private readonly TimeSpan _quietWindow;
    private readonly Timer _timer;
    private bool _windowOpen;
    private bool _disposed;

    /// <summary>
    /// Creates a debouncer that invokes <paramref name="onFlush"/> with the distinct keys collected
    /// during each quiet window.
    /// </summary>
    /// <param name="onFlush">The callback invoked once per window with the distinct keys collected.</param>
    /// <param name="quietWindow">The window duration; defaults to 200 milliseconds when null.</param>
    public NotificationDebouncer(Action<IReadOnlyCollection<TKey>> onFlush, TimeSpan? quietWindow = null)
    {
        ArgumentNullException.ThrowIfNull(onFlush);
        _onFlush = onFlush;
        _quietWindow = quietWindow ?? DefaultQuietWindow;
        _timer = new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Records a notification for the given key. Duplicate keys within the same window are coalesced.
    /// No-op after disposal.
    /// </summary>
    public void Notify(TKey key)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _pendingKeys.Add(key);
            if (_windowOpen)
                return;

            _windowOpen = true;
            _timer.Change(_quietWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        TKey[] keys;
        lock (_lock)
        {
            if (_disposed || _pendingKeys.Count == 0)
                return;

            _windowOpen = false;
            keys = new TKey[_pendingKeys.Count];
            _pendingKeys.CopyTo(keys);
            _pendingKeys.Clear();
        }

        // Invoked outside the lock so a slow callback never blocks Notify. A throwing callback must be
        // contained here: an unhandled exception in a Timer callback would take down the whole process.
        try
        {
            _onFlush(keys);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "NotificationDebouncer: flush callback threw; the notifications in this window are dropped");
        }
    }

    /// <summary>
    /// Stops all further callbacks and discards any pending keys.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _pendingKeys.Clear();
        }

        _timer.Dispose();
    }
}
