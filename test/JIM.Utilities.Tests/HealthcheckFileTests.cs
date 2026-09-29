// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Utilities.Tests;

/// <summary>
/// The container health-check heartbeat: written from every loop that proves a service alive, so a write that fails
/// must never stop the loop.
/// </summary>
[TestFixture]
public class HealthcheckFileTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"jim-healthcheck-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task TouchAsync_WritablePath_WritesTheCurrentTimeAsync()
    {
        var path = Path.Combine(_directory, "healthcheck");
        var before = DateTime.UtcNow;

        var written = await HealthcheckFile.TouchAsync(path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.True);
            var stamp = DateTime.Parse(await File.ReadAllTextAsync(path), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Assert.That(stamp, Is.GreaterThanOrEqualTo(before));
        }
    }

    [Test]
    public async Task TouchAsync_UnwritablePath_ReportsFailureWithoutThrowingAsync()
    {
        // A missing directory is the simplest file-system failure to arrange; a full disk or a read-only mount
        // surfaces the same way.
        var path = Path.Combine(_directory, "missing", "healthcheck");

        var written = await HealthcheckFile.TouchAsync(path);

        Assert.That(written, Is.False);
    }

    // A start-up step can outlast a health check's threshold (migrations, warming caches), and Podman restarts a
    // container whose liveness check fails, so the heartbeat must stay fresh for as long as the step runs.
    [Test]
    public async Task KeepFreshWhileAsync_WorkStillRunning_TouchesRepeatedlyAsync()
    {
        var touches = 0;
        var touchedThreeTimes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> Touch()
        {
            if (Interlocked.Increment(ref touches) >= 3)
                touchedThreeTimes.TrySetResult();
            return Task.FromResult(true);
        }

        // The work finishes only once the heartbeat has been touched three times while it ran.
        var keepFresh = HealthcheckFile.KeepFreshWhileAsync(() => touchedThreeTimes.Task, TimeSpan.FromMilliseconds(10), Touch);

        Assert.That(await Task.WhenAny(keepFresh, Task.Delay(TimeSpan.FromSeconds(30))), Is.SameAs(keepFresh),
            "the heartbeat was not touched repeatedly while the work ran");
        await keepFresh;
        Assert.That(touches, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public async Task KeepFreshWhileAsync_WorkFinished_StopsTouchingAsync()
    {
        var touches = 0;
        Task<bool> Touch()
        {
            Interlocked.Increment(ref touches);
            return Task.FromResult(true);
        }

        await HealthcheckFile.KeepFreshWhileAsync(() => Task.Delay(50), TimeSpan.FromMilliseconds(10), Touch);
        var touchesWhenFinished = Volatile.Read(ref touches);
        await Task.Delay(200);

        // Once the step is over, the service's own loop is what proves it alive; a leftover touch would hide a hang.
        Assert.That(Volatile.Read(ref touches), Is.EqualTo(touchesWhenFinished));
    }

    [Test]
    public async Task KeepFreshWhileAsync_WorkFails_PropagatesTheFailureAndStopsTouchingAsync()
    {
        var touches = 0;
        Task<bool> Touch()
        {
            Interlocked.Increment(ref touches);
            return Task.FromResult(true);
        }

        async Task FailingWork()
        {
            await Task.Delay(20);
            throw new InvalidOperationException("migration failed");
        }

        var failure = Assert.ThrowsAsync<InvalidOperationException>(
            () => HealthcheckFile.KeepFreshWhileAsync(FailingWork, TimeSpan.FromMilliseconds(10), Touch));
        var touchesWhenFailed = Volatile.Read(ref touches);
        await Task.Delay(200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Is.EqualTo("migration failed"));
            Assert.That(Volatile.Read(ref touches), Is.EqualTo(touchesWhenFailed));
        }
    }

    [Test]
    public async Task KeepFreshWhileAsync_TouchFails_LetsTheWorkFinishAsync()
    {
        var workRan = false;

        await HealthcheckFile.KeepFreshWhileAsync(async () =>
        {
            await Task.Delay(50);
            workRan = true;
        }, TimeSpan.FromMilliseconds(10), () => Task.FromResult(false));

        Assert.That(workRan, Is.True);
    }
}
