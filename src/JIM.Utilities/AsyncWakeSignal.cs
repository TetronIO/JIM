// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Utilities;

/// <summary>
/// A thread-safe asynchronous auto-reset signal for waking a single waiter, such as a polling loop that
/// should react promptly to an external event without giving up its timeout-based fallback.
/// Multiple <see cref="Signal"/> calls before a wait coalesce into a single wake; a wait consumes the
/// pending signal, and subsequent waits block until signalled again or the timeout elapses.
/// </summary>
public sealed class AsyncWakeSignal
{
    private readonly Lock _lock = new();
    private TaskCompletionSource? _waiter;
    private bool _signalled;

    /// <summary>
    /// Signals the current waiter, or records a pending signal if no wait is in progress so the next
    /// wait completes immediately. Idempotent; repeated signals before a wait coalesce into one wake.
    /// </summary>
    public void Signal()
    {
        TaskCompletionSource? waiterToComplete;
        lock (_lock)
        {
            if (_waiter != null)
            {
                waiterToComplete = _waiter;
                _waiter = null;
            }
            else
            {
                _signalled = true;
                waiterToComplete = null;
            }
        }

        // Complete outside the lock; RunContinuationsAsynchronously means continuations do not run
        // inline here, but keeping the completion out of the critical section is still good hygiene.
        waiterToComplete?.TrySetResult();
    }

    /// <summary>
    /// Waits until the signal is set or the timeout elapses. Returns true when woken by a signal
    /// (consuming it), false when the timeout elapsed with no signal.
    /// </summary>
    /// <exception cref="OperationCanceledException">The cancellation token was cancelled.</exception>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource waiter;
        lock (_lock)
        {
            if (_signalled)
            {
                _signalled = false;
                return true;
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiter = waiter;
        }

        try
        {
            await waiter.Task.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            ReleaseWaiter(waiter);
            return false;
        }
        catch (OperationCanceledException)
        {
            ReleaseWaiter(waiter);
            throw;
        }
    }

    /// <summary>
    /// Detaches a waiter that gave up (timeout or cancellation). If a concurrent <see cref="Signal"/>
    /// had already dequeued and completed the waiter, the signal is preserved for the next wait rather
    /// than being lost.
    /// </summary>
    private void ReleaseWaiter(TaskCompletionSource waiter)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_waiter, waiter))
                _waiter = null;
            else if (waiter.Task.IsCompletedSuccessfully)
                _signalled = true;
        }
    }
}
