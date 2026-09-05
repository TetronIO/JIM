// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data;
using JIM.Models.Core;
using JIM.Models.Tasking;
using JIM.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace JIM.Web.Services;

/// <summary>
/// Background service that bridges PostgreSQL NOTIFY events into JIM.Web (issue #307). It listens on the
/// Worker Task change, Activity progress and Password Synchronisation queue channels via
/// <see cref="IDatabaseNotificationListener"/> and fans each notification out to the in-process
/// <see cref="UiNotificationService"/> (for Blazor Server components) and, for the first two, the
/// <see cref="JimNotificationHub"/> SignalR hub (for non-Blazor consumers). Activity progress and password queue
/// bursts are debounced so a busy synchronisation run or a bulk enqueue cannot flood the UI with re-renders. The
/// listener's connection state is mirrored into <see cref="UiNotificationService"/> so components can
/// fall back to fast polling while real-time updates are unavailable. This service must never crash the
/// application: every failure path is contained and the UI's polling fallback covers any gap.
/// </summary>
public sealed class NotificationListenerService : BackgroundService
{
    private static readonly TimeSpan ActivityProgressQuietWindow = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PasswordChangeQuietWindow = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ListenerRestartDelay = TimeSpan.FromSeconds(5);

    private readonly IDatabaseNotificationListener _listener;
    private readonly UiNotificationService _uiNotificationService;
    private readonly IHubContext<JimNotificationHub> _hubContext;
    private readonly ILogger<NotificationListenerService> _logger;
    private NotificationDebouncer<Guid>? _activityProgressDebouncer;
    private NotificationDebouncer<int>? _passwordChangeDebouncer;
    private CancellationToken _stoppingToken;

    public NotificationListenerService(
        IDatabaseNotificationListener listener,
        UiNotificationService uiNotificationService,
        IHubContext<JimNotificationHub> hubContext,
        ILogger<NotificationListenerService> logger)
    {
        _listener = listener;
        _uiNotificationService = uiNotificationService;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _activityProgressDebouncer = new NotificationDebouncer<Guid>(HandleActivityProgressFlush, ActivityProgressQuietWindow);
        _passwordChangeDebouncer = new NotificationDebouncer<int>(HandlePasswordChangeFlush, PasswordChangeQuietWindow);
        _listener.ConnectionStateChanged += OnListenerConnectionStateChanged;
        _uiNotificationService.SetRealTimeAvailability(_listener.IsConnected);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _listener.ListenAsync(
                        [
                            Constants.NotificationChannels.WorkerTaskChange,
                            Constants.NotificationChannels.ActivityProgress,
                            Constants.NotificationChannels.PasswordChange
                        ],
                        HandleNotificationAsync,
                        stoppingToken);

                    // ListenAsync only returns normally on cancellation.
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Resilience boundary: the listener reconnects internally, so reaching here means an
                    // unexpected failure escaped it. It must never take JIM.Web down; the UI's polling
                    // fallback keeps working without real-time updates while we restart the listener.
                    _logger.LogError(ex, "NotificationListenerService: listener failed unexpectedly; restarting in {Delay}", ListenerRestartDelay);
                    _uiNotificationService.SetRealTimeAvailability(false);

                    try
                    {
                        await Task.Delay(ListenerRestartDelay, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            _listener.ConnectionStateChanged -= OnListenerConnectionStateChanged;
            _uiNotificationService.SetRealTimeAvailability(false);
        }
    }

    public override void Dispose()
    {
        _activityProgressDebouncer?.Dispose();
        _passwordChangeDebouncer?.Dispose();
        base.Dispose();
    }

    private async Task HandleNotificationAsync(string channel, string payload, CancellationToken cancellationToken)
    {
        switch (channel)
        {
            case Constants.NotificationChannels.WorkerTaskChange:
                if (!WorkerTaskChangeNotification.TryParse(payload, out var notification))
                {
                    _logger.LogWarning("NotificationListenerService: malformed Worker Task change payload received; ignoring");
                    return;
                }

                _uiNotificationService.PublishWorkerTaskChange(notification!);
                await BroadcastAsync(JimNotificationHub.WorkerTaskChangedMethod, notification, cancellationToken);
                break;

            case Constants.NotificationChannels.ActivityProgress:
                if (!Guid.TryParse(payload, out var activityId))
                {
                    _logger.LogWarning("NotificationListenerService: malformed Activity progress payload received; ignoring");
                    return;
                }

                _activityProgressDebouncer?.Notify(activityId);
                break;

            case Constants.NotificationChannels.PasswordChange:
                // The payload is the Connected System id the queue row belongs to (#1635). Nothing about the
                // change itself travels: a subscriber re-reads the queue, so a malformed id is dropped rather than
                // published as zero and read as "system 0 changed".
                if (!int.TryParse(payload, out var connectedSystemId))
                {
                    _logger.LogWarning("NotificationListenerService: malformed Password Synchronisation queue payload received; ignoring");
                    return;
                }

                _passwordChangeDebouncer?.Notify(connectedSystemId);
                break;
        }
    }

    private void OnListenerConnectionStateChanged(bool connected)
    {
        _uiNotificationService.SetRealTimeAvailability(connected);
    }

    /// <summary>
    /// Debouncer flush callback: publishes each coalesced Activity id to the in-process relay
    /// synchronously, then broadcasts to SignalR clients on a background task (the flush callback is
    /// synchronous, hub sends are not).
    /// </summary>
    private void HandleActivityProgressFlush(IReadOnlyCollection<Guid> activityIds)
    {
        foreach (var activityId in activityIds)
            _uiNotificationService.PublishActivityProgress(activityId);

        _ = BroadcastActivityProgressAsync(activityIds);
    }

    /// <summary>
    /// Debouncer flush callback for the Password Synchronisation queue: publishes each coalesced Connected System id
    /// to the in-process relay. There is no hub broadcast; the only consumers are in-process waiters.
    /// </summary>
    private void HandlePasswordChangeFlush(IReadOnlyCollection<int> connectedSystemIds)
    {
        foreach (var connectedSystemId in connectedSystemIds)
            _uiNotificationService.PublishPasswordChange(connectedSystemId);
    }

    private async Task BroadcastActivityProgressAsync(IReadOnlyCollection<Guid> activityIds)
    {
        foreach (var activityId in activityIds)
            await BroadcastAsync(JimNotificationHub.ActivityProgressChangedMethod, activityId, _stoppingToken);
    }

    /// <summary>
    /// Broadcasts a single hub method invocation to all connected SignalR clients, containing failures:
    /// a hub send failure must never disturb the listener loop or the in-process relay.
    /// </summary>
    private async Task BroadcastAsync(string methodName, object? argument, CancellationToken cancellationToken)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync(methodName, argument, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NotificationListenerService: SignalR broadcast of {MethodName} failed", methodName);
        }
    }
}
