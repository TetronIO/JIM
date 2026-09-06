// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JIM.Data;
using JIM.Models.Core;
using JIM.Web.Hubs;
using JIM.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The relay's Password Synchronisation leg (#1635): a <c>jim_password_change</c> notification from the database
/// reaches in-process subscribers as the Connected System id it named, coalesced the way Activity progress is, so
/// a burst of queue writes for one system costs one re-read rather than one per row.
/// </summary>
[TestFixture]
public class NotificationListenerServicePasswordChangeTests
{
    private static readonly TimeSpan FlushWait = TimeSpan.FromSeconds(5);

    private FakeListener _listener = null!;
    private UiNotificationService _relay = null!;
    private NotificationListenerService _service = null!;
    private List<int> _received = null!;
    private SemaphoreSlim _receivedSignal = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        _listener = new FakeListener();
        _relay = new UiNotificationService(new Mock<ILogger<UiNotificationService>>().Object);
        _received = [];
        _receivedSignal = new SemaphoreSlim(0);
        _relay.PasswordChangeChanged += id =>
        {
            lock (_received)
                _received.Add(id);
            _receivedSignal.Release();
        };

        _service = new NotificationListenerService(
            _listener,
            _relay,
            new Mock<IHubContext<JimNotificationHub>>().Object,
            new Mock<ILogger<NotificationListenerService>>().Object);

        await _service.StartAsync(CancellationToken.None);
        await _listener.Started.Task.WaitAsync(FlushWait);
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        _receivedSignal.Dispose();
    }

    [Test]
    public void ExecuteAsync_ListensOnThePasswordChangeChannel()
    {
        Assert.That(_listener.Channels, Does.Contain(Constants.NotificationChannels.PasswordChange));
    }

    [Test]
    public async Task PasswordChangeNotification_PublishesTheConnectedSystemIdAsync()
    {
        await _listener.RaiseAsync(Constants.NotificationChannels.PasswordChange, "7");

        Assert.That(await _receivedSignal.WaitAsync(FlushWait), Is.True, "The relay never published the notification.");
        lock (_received)
            Assert.That(_received, Is.EqualTo(new[] { 7 }));
    }

    [Test]
    public async Task PasswordChangeNotifications_ForOneSystemInABurst_AreCoalescedAsync()
    {
        await _listener.RaiseAsync(Constants.NotificationChannels.PasswordChange, "7");
        await _listener.RaiseAsync(Constants.NotificationChannels.PasswordChange, "7");
        await _listener.RaiseAsync(Constants.NotificationChannels.PasswordChange, "7");

        Assert.That(await _receivedSignal.WaitAsync(FlushWait), Is.True);
        // Give a second flush every chance to arrive before asserting it did not.
        await Task.Delay(400);
        lock (_received)
            Assert.That(_received, Has.Count.EqualTo(1), "Three writes to one system's queue are one hint, not three.");
    }

    [Test]
    public async Task PasswordChangeNotification_MalformedPayload_IsIgnoredAsync()
    {
        await _listener.RaiseAsync(Constants.NotificationChannels.PasswordChange, "not-a-number");

        Assert.That(await _receivedSignal.WaitAsync(TimeSpan.FromMilliseconds(500)), Is.False,
            "A payload that is not a Connected System id must be dropped, not published as zero.");
    }

    /// <summary>
    /// A listener the test drives: it captures the channels and the handler the service registers and hands
    /// notifications straight to that handler.
    /// </summary>
    private sealed class FakeListener : IDatabaseNotificationListener
    {
        private Func<string, string, CancellationToken, Task>? _handler;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<string> Channels { get; private set; } = [];

        public bool IsConnected => true;

        public event Action<bool>? ConnectionStateChanged;

        public async Task ListenAsync(
            IReadOnlyCollection<string> channelNames,
            Func<string, string, CancellationToken, Task> onNotificationAsync,
            CancellationToken cancellationToken)
        {
            Channels = channelNames;
            _handler = onNotificationAsync;
            Started.TrySetResult();
            ConnectionStateChanged?.Invoke(true);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Returning normally on cancellation is the contract.
            }
        }

        public Task RaiseAsync(string channel, string payload) =>
            _handler?.Invoke(channel, payload, CancellationToken.None) ?? Task.CompletedTask;
    }
}
