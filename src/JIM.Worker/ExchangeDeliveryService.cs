// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Models.Core;
using JIM.Models.Operations;
using JIM.Worker.Delivery;
using JIM.Worker.ExchangeDelivery;

namespace JIM.Worker;

/// <summary>
/// The Exchange Delivery Service (#2021): delivers the Exchange queue on its own clock, so the recipient state an
/// export left for an account reaches Exchange within seconds, without waiting for another export run (PRD
/// requirement 10). It reports its heartbeat under <see cref="JimService.WorkerExchange"/>, beside the password
/// delivery service rather than inside it, so a fault in one cannot take the other down; how it is hosted, woken and
/// kept alive is <see cref="DeliveryService"/>; its work against JIM is <see cref="JimExchangeDeliveryWork"/>.
/// </summary>
internal sealed class ExchangeDeliveryService : DeliveryService
{
    public ExchangeDeliveryService(IJimApplicationFactory jimFactory, IDatabaseNotificationListener notificationListener)
        : base(jimFactory, notificationListener)
    {
    }

    protected override string ServiceName => "Exchange Delivery Service";

    protected override string SchedulerName => "Exchange delivery";

    protected override string NotificationChannel => Constants.NotificationChannels.ExchangeChange;

    protected override JimService HeartbeatService => JimService.WorkerExchange;

    protected override IDeliveryWork CreateWork(ServiceHeartbeatWriter heartbeat) => new JimExchangeDeliveryWork(JimFactory, heartbeat);
}
