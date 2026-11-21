// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Graph;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class GraphRequestPacerTests
{
    private DateTimeOffset _now;
    private List<TimeSpan> _delays = null!;

    [SetUp]
    public void SetUp()
    {
        _now = new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);
        _delays = [];
    }

    [Test]
    public async Task WaitForWriteSlotsAsync_FirstWrite_DoesNotWaitAsync()
    {
        var pacer = CreatePacer(10);

        await pacer.WaitForWriteSlotsAsync(1, CancellationToken.None);

        Assert.That(_delays, Is.Empty);
    }

    [Test]
    public async Task WaitForWriteSlotsAsync_BackToBackWrites_AreSpacedAtTheRateAsync()
    {
        var pacer = CreatePacer(10);

        await pacer.WaitForWriteSlotsAsync(1, CancellationToken.None);
        await pacer.WaitForWriteSlotsAsync(1, CancellationToken.None);
        await pacer.WaitForWriteSlotsAsync(1, CancellationToken.None);

        Assert.That(_delays, Is.EqualTo(new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100) }));
    }

    [Test]
    public async Task WaitForWriteSlotsAsync_ABatchReservesASlotPerWriteAsync()
    {
        // A $batch of 20 writes spends 20 writes of the tenant's quota, so the next request waits for all of them.
        var pacer = CreatePacer(10);

        await pacer.WaitForWriteSlotsAsync(20, CancellationToken.None);
        await pacer.WaitForWriteSlotsAsync(1, CancellationToken.None);

        Assert.That(_delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(2) }));
    }

    [Test]
    public async Task WaitForWriteSlotsAsync_AfterAnIdlePeriod_DoesNotWaitAsync()
    {
        var pacer = CreatePacer(10);

        await pacer.WaitForWriteSlotsAsync(1, CancellationToken.None);
        _now = _now.AddSeconds(5);
        await pacer.WaitForWriteSlotsAsync(1, CancellationToken.None);

        Assert.That(_delays, Is.Empty);
    }

    [Test]
    public async Task WaitForWriteSlotsAsync_NoWrites_DoesNotWaitOrReserveAsync()
    {
        var pacer = CreatePacer(10);

        await pacer.WaitForWriteSlotsAsync(1, CancellationToken.None);
        await pacer.WaitForWriteSlotsAsync(0, CancellationToken.None);

        Assert.That(_delays, Is.Empty);
    }

    [TestCase(0.85, 2)]
    [TestCase(1.0, 4)]
    [TestCase(1.8, 4)]
    public void ObserveThrottleLimitPercentage_UnderPressure_SlowsWrites(double percentage, double expectedFactor)
    {
        var pacer = CreatePacer(10);

        pacer.ObserveThrottleLimitPercentage(percentage);

        Assert.That(pacer.SlowdownFactor, Is.EqualTo(expectedFactor));
    }

    [Test]
    public void ObserveThrottleLimitPercentage_PressureGone_RestoresTheConfiguredRate()
    {
        // Graph sends the header only above 80% of the limit, so its absence means the pressure has passed.
        var pacer = CreatePacer(10);
        pacer.ObserveThrottleLimitPercentage(1.2);

        pacer.ObserveThrottleLimitPercentage(null);

        Assert.That(pacer.SlowdownFactor, Is.EqualTo(1));
    }

    [Test]
    public async Task WaitForWriteSlotsAsync_UnderPressure_SpacesWritesFurtherApartAsync()
    {
        var pacer = CreatePacer(10);
        pacer.ObserveThrottleLimitPercentage(0.9);

        await pacer.WaitForWriteSlotsAsync(1, CancellationToken.None);
        await pacer.WaitForWriteSlotsAsync(1, CancellationToken.None);

        Assert.That(_delays, Is.EqualTo(new[] { TimeSpan.FromMilliseconds(200) }));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(21)]
    public void Constructor_RateOutsideTheWriteQuota_Throws(double writesPerSecond)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new GraphRequestPacer(writesPerSecond));
    }

    private GraphRequestPacer CreatePacer(double writesPerSecond) => new(writesPerSecond, () => _now, (delay, _) =>
    {
        _delays.Add(delay);
        _now += delay;
        return Task.CompletedTask;
    });
}
