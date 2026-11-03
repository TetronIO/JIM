// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Models.Core;
using JIM.Models.Operations;
using JIM.Worker.Delivery;
using JIM.Worker.PasswordDelivery;

namespace JIM.Worker;

/// <summary>
/// The Password Delivery Service (#1635): delivers the Password Synchronisation queue on its own clock, so a
/// person's reset reaches their accounts in about a second whatever the synchronisation loop is doing. It reports
/// its heartbeat under <see cref="JimService.WorkerDelivery"/>; how it is hosted, woken and kept alive is
/// <see cref="DeliveryService"/>.
/// </summary>
internal sealed class PasswordDeliveryService : DeliveryService
{
    public PasswordDeliveryService(IJimApplicationFactory jimFactory, IDatabaseNotificationListener notificationListener)
        : base(jimFactory, notificationListener)
    {
    }

    protected override string ServiceName => "Password Delivery Service";

    protected override string SchedulerName => "Password delivery";

    protected override string NotificationChannel => Constants.NotificationChannels.PasswordChange;

    protected override JimService HeartbeatService => JimService.WorkerDelivery;

    protected override IDeliveryWork CreateWork(ServiceHeartbeatWriter heartbeat) => new JimPasswordDeliveryWork(JimFactory, heartbeat);
}
