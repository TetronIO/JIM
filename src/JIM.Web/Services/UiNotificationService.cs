// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Tasking;

namespace JIM.Web.Services;

/// <summary>
/// Singleton implementation of <see cref="IUiNotificationService"/> (issue #307). The
/// <c>NotificationListenerService</c> calls the internal publish methods as PostgreSQL NOTIFY events
/// arrive; Blazor Server components subscribe to the events. Each subscriber is invoked in isolation so
/// one faulty subscriber cannot prevent others from being notified.
/// </summary>
public sealed class UiNotificationService : IUiNotificationService
{
    private readonly ILogger<UiNotificationService> _logger;

    public UiNotificationService(ILogger<UiNotificationService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public event Action<WorkerTaskChangeNotification>? WorkerTaskChanged;

    /// <inheritdoc />
    public event Action<Guid>? ActivityProgressChanged;

    /// <inheritdoc />
    public bool IsRealTimeAvailable { get; private set; }

    /// <inheritdoc />
    public event Action<bool>? RealTimeAvailabilityChanged;

    /// <summary>
    /// Raises <see cref="WorkerTaskChanged"/> for all subscribers, isolating subscriber exceptions.
    /// </summary>
    internal void PublishWorkerTaskChange(WorkerTaskChangeNotification notification)
    {
        RaiseIsolated(WorkerTaskChanged, notification, nameof(WorkerTaskChanged));
    }

    /// <summary>
    /// Raises <see cref="ActivityProgressChanged"/> for all subscribers, isolating subscriber exceptions.
    /// </summary>
    internal void PublishActivityProgress(Guid activityId)
    {
        RaiseIsolated(ActivityProgressChanged, activityId, nameof(ActivityProgressChanged));
    }

    /// <summary>
    /// Updates <see cref="IsRealTimeAvailable"/> and raises <see cref="RealTimeAvailabilityChanged"/>
    /// when the value actually changed.
    /// </summary>
    internal void SetRealTimeAvailability(bool available)
    {
        if (IsRealTimeAvailable == available)
            return;

        IsRealTimeAvailable = available;
        RaiseIsolated(RealTimeAvailabilityChanged, available, nameof(RealTimeAvailabilityChanged));
    }

    /// <summary>
    /// Invokes each subscriber of a multicast event individually so that one throwing subscriber (for
    /// example, a component mid-teardown) cannot stop the remaining subscribers being notified.
    /// </summary>
    private void RaiseIsolated<T>(Action<T>? handlers, T argument, string eventName)
    {
        if (handlers == null)
            return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(argument);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "A {EventName} subscriber threw; other subscribers are unaffected", eventName);
            }
        }
    }
}
