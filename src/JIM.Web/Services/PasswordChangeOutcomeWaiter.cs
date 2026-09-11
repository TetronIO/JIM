// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Models.Transactional.DTOs;

namespace JIM.Web.Services;

/// <summary>
/// Singleton implementation of <see cref="IPasswordChangeOutcomeWaiter"/> (#1635).
/// <para>
/// Reads the change's outcomes at once and returns if they are settled. Otherwise it re-reads whenever the relay
/// reports the Password Synchronisation queue moving (any system: a change's targets are few and a re-read is
/// three cheap queries, so filtering by system buys nothing worth the risk of missing one) or the change's own
/// Activity progressing, and on a timer as a safety net: every second while real-time updates are unavailable,
/// every five seconds while they are, because a notification can still be lost across a listener reconnect.
/// </para>
/// <para>
/// Every read opens its own <c>JimApplication</c>. This is a singleton living for the process, and a DbContext held
/// across a wait would be a context held across seconds of idling, on a scoped lifetime it does not have.
/// </para>
/// </summary>
public sealed class PasswordChangeOutcomeWaiter : IPasswordChangeOutcomeWaiter
{
    private static readonly TimeSpan RealTimePollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(1);

    private readonly IJimApplicationFactory _jimFactory;
    private readonly IUiNotificationService _uiNotifications;
    private readonly IPasswordChangeNotifications _passwordChangeNotifications;
    private readonly ILogger<PasswordChangeOutcomeWaiter> _logger;

    public PasswordChangeOutcomeWaiter(
        IJimApplicationFactory jimFactory,
        IUiNotificationService uiNotifications,
        IPasswordChangeNotifications passwordChangeNotifications,
        ILogger<PasswordChangeOutcomeWaiter> logger)
    {
        _jimFactory = jimFactory;
        _uiNotifications = uiNotifications;
        _passwordChangeNotifications = passwordChangeNotifications;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PasswordChangeOutcomes?> WaitForOutcomesAsync(Guid activityId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var latest = await ReadAsync(activityId);
        if (latest == null || latest.IsSettled || timeout <= TimeSpan.Zero)
            return latest;

        var deadline = DateTime.UtcNow + timeout;

        // One signal per wait, replaced before each re-read rather than after it, so a notification arriving while
        // a read is in flight sets the next signal and earns another read instead of being lost.
        var wake = NewSignal();
        void OnPasswordChangeChanged(int _) => wake.TrySetResult();
        void OnActivityProgressChanged(Guid changedActivityId)
        {
            if (changedActivityId == activityId)
                wake.TrySetResult();
        }

        _passwordChangeNotifications.PasswordChangeChanged += OnPasswordChangeChanged;
        _uiNotifications.ActivityProgressChanged += OnActivityProgressChanged;
        try
        {
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return latest;

                var pollInterval = _uiNotifications.IsRealTimeAvailable ? RealTimePollInterval : FallbackPollInterval;
                var delay = remaining < pollInterval ? remaining : pollInterval;

                using (var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var delayTask = Task.Delay(delay, delayCancellation.Token);
                    var completed = await Task.WhenAny(wake.Task, delayTask);
                    if (completed == delayTask)
                    {
                        // Surfaces the caller's cancellation as an exception; a plain timer tick falls through.
                        await delayTask;
                    }
                    else
                    {
                        // Stop the timer we are no longer waiting on rather than leaving it to run out on its own.
                        delayCancellation.Cancel();
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                wake = NewSignal();

                var reread = await ReadAsync(activityId);
                if (reread == null)
                {
                    // The Activity has gone (retention, or a deletion racing this wait). What was last seen is
                    // still the truest answer available, so that is what a timeout returns.
                    _logger.LogWarning("PasswordChangeOutcomeWaiter: Activity {ActivityId} disappeared while being waited on", activityId);
                    continue;
                }

                latest = reread;
                if (latest.IsSettled)
                    return latest;
            }
        }
        finally
        {
            _passwordChangeNotifications.PasswordChangeChanged -= OnPasswordChangeChanged;
            _uiNotifications.ActivityProgressChanged -= OnActivityProgressChanged;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<PasswordChangeOutcomes?> ReadAsync(Guid activityId)
    {
        using var jim = _jimFactory.Create();
        return await jim.PasswordSynchronisation.GetChangeOutcomesAsync(activityId);
    }
}
