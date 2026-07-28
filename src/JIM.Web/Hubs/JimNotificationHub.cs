// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace JIM.Web.Hubs;

/// <summary>
/// SignalR hub for real-time operational notifications (issue #307), mapped at
/// <c>/hubs/notifications</c>. It exists for non-Blazor consumers (external dashboards, tooling);
/// Blazor Server components receive the same events in-process via <c>IUiNotificationService</c> and do
/// not connect to this hub. Broadcasts are server-to-client only, sent by
/// <c>NotificationListenerService</c> as database NOTIFY events arrive, so the hub defines no
/// client-invokable methods.
/// </summary>
[Authorize(Roles = "Administrator")]
public class JimNotificationHub : Hub
{
    /// <summary>
    /// The client method invoked when a Worker Task is inserted, changes status, or is deleted.
    /// The argument is the parsed <c>WorkerTaskChangeNotification</c>.
    /// </summary>
    public const string WorkerTaskChangedMethod = "WorkerTaskChanged";

    /// <summary>
    /// The client method invoked when an Activity's progress or status changes. The argument is the
    /// Activity id. Bursts are debounced server-side, so clients receive at most one invocation per
    /// Activity per debounce window.
    /// </summary>
    public const string ActivityProgressChangedMethod = "ActivityProgressChanged";
}
