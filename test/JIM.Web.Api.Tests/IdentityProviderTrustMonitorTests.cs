// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Threading;
using System.Threading.Tasks;
using JIM.Models.Security;
using JIM.Web.Authentication;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The identity provider's last known status, shared by the sign-in unavailable page, the banner and the API (#2363).
/// The status endpoint is anonymous, so anyone could otherwise make JIM connect to the identity provider as fast as
/// they could send requests; checks are floored, and only an administrator's action forces one.
/// </summary>
[TestFixture]
public class IdentityProviderTrustMonitorTests
{
    private ManualTimeProvider _time = null!;
    private int _checks;
    private IdentityProviderTrustState _nextState;

    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider();
        _checks = 0;
        _nextState = IdentityProviderTrustState.Untrusted;
    }

    [Test]
    public void Current_BeforeAnyCheck_NotChecked()
    {
        var monitor = NewMonitor();

        Assert.That(monitor.Current.State, Is.EqualTo(IdentityProviderTrustState.NotChecked));
    }

    [Test]
    public async Task GetFreshStatusAsync_NeverChecked_ChecksAsync()
    {
        var monitor = NewMonitor();

        var status = await monitor.GetFreshStatusAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.State, Is.EqualTo(IdentityProviderTrustState.Untrusted));
            Assert.That(_checks, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task GetFreshStatusAsync_CheckedWithinTheFloor_AnswersFromMemoryAsync()
    {
        var monitor = NewMonitor();
        await monitor.GetFreshStatusAsync(CancellationToken.None);

        _time.Advance(IdentityProviderTrustMonitor.CheckFloor - TimeSpan.FromMilliseconds(1));
        await monitor.GetFreshStatusAsync(CancellationToken.None);

        Assert.That(_checks, Is.EqualTo(1));
    }

    [Test]
    public async Task GetFreshStatusAsync_AfterTheFloor_ChecksAgainAndSeesTheChangeAsync()
    {
        var monitor = NewMonitor();
        await monitor.GetFreshStatusAsync(CancellationToken.None);

        _nextState = IdentityProviderTrustState.Trusted;
        _time.Advance(IdentityProviderTrustMonitor.CheckFloor);
        var status = await monitor.GetFreshStatusAsync(CancellationToken.None);

        Assert.That(status.State, Is.EqualTo(IdentityProviderTrustState.Trusted));
    }

    [Test]
    public async Task RecheckAsync_WithinTheFloor_ChecksAnywayAsync()
    {
        var monitor = NewMonitor();
        await monitor.GetFreshStatusAsync(CancellationToken.None);

        _nextState = IdentityProviderTrustState.Trusted;
        var status = await monitor.RecheckAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.State, Is.EqualTo(IdentityProviderTrustState.Trusted));
            Assert.That(monitor.Current.State, Is.EqualTo(IdentityProviderTrustState.Trusted));
        }
    }

    [Test]
    public async Task GetFreshStatusAsync_CheckFails_KeepsTheLastStatusAsync()
    {
        var monitor = NewMonitor();
        await monitor.GetFreshStatusAsync(CancellationToken.None);

        var failing = new IdentityProviderTrustMonitor(_ => throw new InvalidOperationException("database unavailable"), _time);
        Assert.That(async () => await failing.GetFreshStatusAsync(CancellationToken.None), Throws.Nothing);
        Assert.That(failing.Current.State, Is.EqualTo(IdentityProviderTrustState.NotChecked));
    }

    private IdentityProviderTrustMonitor NewMonitor() => new(_ =>
    {
        _checks++;
        return Task.FromResult(new IdentityProviderTrustStatus { State = _nextState, CheckedUtc = _time.GetUtcNow().UtcDateTime });
    }, _time);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
