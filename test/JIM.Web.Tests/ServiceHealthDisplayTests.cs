// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Operations;
using JIM.Web.Models;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The words the strip, the banner and the Administration index put on a service's health, kept in one place so
/// the three surfaces cannot disagree. These pin the sentences an administrator reads at three in the morning:
/// which service is named, how long it has been gone, and what that means for queued work.
/// </summary>
[TestFixture]
public class ServiceHealthDisplayTests
{
    private static readonly DateTime AsOf = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    [TestCase(JimService.WorkerSync, "Worker · Sync")]
    [TestCase(JimService.WorkerPasswordDelivery, "Worker · Passwords")]
    [TestCase(JimService.Scheduler, "Scheduler")]
    public void Label_EachService_IsTheCardTitle(JimService service, string expected)
    {
        Assert.That(ServiceHealthDisplay.Label(service), Is.EqualTo(expected));
    }

    [TestCase(ServiceHealthState.Running, "Running", Color.Success, "running")]
    [TestCase(ServiceHealthState.Stale, "Stale", Color.Warning, "stale")]
    [TestCase(ServiceHealthState.NoProgress, "No progress", Color.Warning, "no-progress")]
    [TestCase(ServiceHealthState.NotSeen, "Not seen", Color.Error, "not-seen")]
    public void State_EachValue_HasWordColourAndModifier(ServiceHealthState state, string word, Color colour, string modifier)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ServiceHealthDisplay.StateWord(state), Is.EqualTo(word));
            Assert.That(ServiceHealthDisplay.StateColor(state), Is.EqualTo(colour));
            Assert.That(ServiceHealthDisplay.StateModifier(state), Is.EqualTo(modifier));
        }
    }

    [TestCase(40, "40 s")]
    [TestCase(59, "59 s")]
    [TestCase(60, "1 min")]
    [TestCase(12 * 60, "12 min")]
    [TestCase(5 * 3600, "5 h")]
    [TestCase(3 * 86400, "3 d")]
    [TestCase(-5, "0 s")]
    public void CompactDuration_RoundsDownToOneUnit(int seconds, string expected)
    {
        Assert.That(ServiceHealthDisplay.CompactDuration(TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));
    }

    [TestCase(1, "1 second")]
    [TestCase(4 * 60, "4 minutes")]
    [TestCase(60, "1 minute")]
    [TestCase(2 * 3600, "2 hours")]
    [TestCase(86400, "1 day")]
    public void LongDuration_RoundsDownToOneUnitInFullWords(int seconds, string expected)
    {
        Assert.That(ServiceHealthDisplay.LongDuration(TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));
    }

    [Test]
    public void Headline_ServiceWithCurrentWork_IsTheWork()
    {
        var service = Derive(JimService.WorkerSync, ageSeconds: 2, currentWork: "Full Import: Corporate Directory");

        Assert.That(ServiceHealthDisplay.Headline(service, AsOf), Is.EqualTo("Full Import: Corporate Directory"));
    }

    [Test]
    public void Headline_RunningServiceWithoutWork_IsIdle()
    {
        var service = Derive(JimService.Scheduler, ageSeconds: 2);

        Assert.That(ServiceHealthDisplay.Headline(service, AsOf), Is.EqualTo("Idle"));
    }

    [Test]
    public void Headline_NotSeenService_SaysWhenTheLastHeartbeatWas()
    {
        var service = Derive(JimService.WorkerSync, ageSeconds: 4 * 60, currentWork: "Full Import: Corporate Directory");

        Assert.That(ServiceHealthDisplay.Headline(service, AsOf), Is.EqualTo("Last heartbeat 4 min ago"),
            "a dead process's last words about its work are not what it is doing now");
    }

    [Test]
    public void Headline_NeverReportedService_SaysSo()
    {
        var service = SystemHealthServer.Derive(JimService.WorkerPasswordDelivery, null, AsOf);

        Assert.That(ServiceHealthDisplay.Headline(service, AsOf), Is.EqualTo("Never reported"));
    }

    [Test]
    public void Banner_EveryServiceRunning_IsNothing()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 2),
            Derive(JimService.WorkerPasswordDelivery, 2),
            Derive(JimService.Scheduler, 2));

        Assert.That(ServiceHealthDisplay.Banner(report), Is.Null);
    }

    [Test]
    public void Banner_StaleService_IsNothing()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 30),
            Derive(JimService.WorkerPasswordDelivery, 2),
            Derive(JimService.Scheduler, 2));

        Assert.That(ServiceHealthDisplay.Banner(report), Is.Null, "a few late heartbeats are worth a glance, not a banner on every page");
    }

    [Test]
    public void Banner_BothWorkerServicesNotSeen_NamesTheWorkerOnce()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 4 * 60),
            Derive(JimService.WorkerPasswordDelivery, 4 * 60),
            Derive(JimService.Scheduler, 2));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(banner!.Severity, Is.EqualTo(Severity.Error));
            Assert.That(banner.Sentence, Is.EqualTo(
                "The Worker has not reported for 4 minutes. Nothing is being synchronised or delivered; queued work is safe and resumes when it returns."));
        }
    }

    [Test]
    public void Banner_WorkerSyncNotSeenAndPasswordDeliveryNeverReported_NamesTheWorkerOnce()
    {
        // The two services share a process: a Worker that is down takes both with it, and an administrator wants
        // to be told once that the Worker is gone, not twice in different words.
        var report = Report(
            Derive(JimService.WorkerSync, 4 * 60),
            SystemHealthServer.Derive(JimService.WorkerPasswordDelivery, null, AsOf),
            Derive(JimService.Scheduler, 2));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        Assert.That(banner!.Sentence, Does.StartWith("The Worker has not reported for 4 minutes."));
    }

    [Test]
    public void Banner_PasswordDeliveryNeverReportedBesideAReportingWorker_IsNothing()
    {
        // A Worker that is heartbeating but whose password delivery loop has never reported is running a version
        // without one; the strip shows that (Not seen, Never reported, the version in amber). A banner on every
        // page would be a permanent alarm about a version gap, not an outage.
        var report = Report(
            Derive(JimService.WorkerSync, 2),
            SystemHealthServer.Derive(JimService.WorkerPasswordDelivery, null, AsOf),
            Derive(JimService.Scheduler, 2));

        Assert.That(ServiceHealthDisplay.Banner(report), Is.Null);
    }

    [Test]
    public void Banner_PasswordDeliveryThatStoppedReporting_IsAnOutage()
    {
        // Distinct from never reported: this loop did exist and has gone quiet while its sibling is still alive.
        var report = Report(
            Derive(JimService.WorkerSync, 2),
            Derive(JimService.WorkerPasswordDelivery, 4 * 60),
            Derive(JimService.Scheduler, 2));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        Assert.That(banner!.Sentence, Is.EqualTo(
            "The Worker's password delivery service has not reported for 4 minutes. Nothing is being delivered; queued work is safe and resumes when it returns."));
    }

    [Test]
    public void Banner_SchedulerNotSeen_NamesTheScheduler()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 2),
            Derive(JimService.WorkerPasswordDelivery, 2),
            Derive(JimService.Scheduler, 3 * 60));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        Assert.That(banner!.Sentence, Is.EqualTo(
            "The Scheduler has not reported for 3 minutes. Nothing is being scheduled; queued work is safe and resumes when it returns."));
    }

    [Test]
    public void Banner_WorkerAndSchedulerNotSeen_NamesBothAndSpeaksInThePlural()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 4 * 60),
            Derive(JimService.WorkerPasswordDelivery, 4 * 60),
            Derive(JimService.Scheduler, 3 * 60));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        Assert.That(banner!.Sentence, Is.EqualTo(
            "The Worker and the Scheduler have not reported for 4 minutes. Nothing is being synchronised, delivered or scheduled; queued work is safe and resumes when they return."));
    }

    [Test]
    public void Banner_EverythingNeverReported_SaysNeverRatherThanForZeroSeconds()
    {
        var report = Report(
            SystemHealthServer.Derive(JimService.WorkerSync, null, AsOf),
            SystemHealthServer.Derive(JimService.WorkerPasswordDelivery, null, AsOf),
            SystemHealthServer.Derive(JimService.Scheduler, null, AsOf));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        Assert.That(banner!.Sentence, Does.StartWith("The Worker and the Scheduler have never reported."));
    }

    [Test]
    public void Banner_WorkerMakingNoProgress_NamesTheWorkAndHowLong()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 2, currentWork: "Full Import: Corporate Directory", progressAgeSeconds: 12 * 60),
            Derive(JimService.WorkerPasswordDelivery, 2),
            Derive(JimService.Scheduler, 2));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(banner!.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(banner.Sentence, Is.EqualTo("The Worker has made no progress on Full Import: Corporate Directory for 12 minutes."));
        }
    }

    [Test]
    public void Banner_NotSeenOutranksNoProgress()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 2, currentWork: "Full Import: Corporate Directory", progressAgeSeconds: 12 * 60),
            Derive(JimService.WorkerPasswordDelivery, 2),
            Derive(JimService.Scheduler, 3 * 60));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(banner!.Severity, Is.EqualTo(Severity.Error));
            Assert.That(banner.Sentence, Does.StartWith("The Scheduler has not reported"));
        }
    }

    [Test]
    public void HasVersionSkew_OnlyWhenAReportedVersionDiffersFromTheWebTier()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ServiceHealthDisplay.HasVersionSkew(Derive(JimService.WorkerSync, 2, version: "0.14.0"), "0.15.0"), Is.True);
            Assert.That(ServiceHealthDisplay.HasVersionSkew(Derive(JimService.WorkerSync, 2, version: "0.15.0"), "0.15.0"), Is.False);
            Assert.That(ServiceHealthDisplay.HasVersionSkew(SystemHealthServer.Derive(JimService.WorkerSync, null, AsOf), "0.15.0"), Is.False,
                "a service that has never reported has no version to disagree with");
        }
    }

    internal static ServiceHealth Derive(
        JimService service,
        int ageSeconds,
        string? currentWork = null,
        int? progressAgeSeconds = null,
        string version = "0.15.0",
        DateTime? asOf = null)
    {
        var at = asOf ?? AsOf;
        var heartbeat = new ServiceHeartbeat
        {
            Service = service,
            InstanceId = "jim-worker-1:4f2a",
            HostName = "jim-worker-1",
            Version = version,
            StartedAt = at.AddDays(-3),
            LastSeenAt = at.AddSeconds(-ageSeconds),
            CurrentWork = currentWork,
            CurrentWorkStartedAt = currentWork == null ? null : at.AddMinutes(-12),
            LastProgressAt = progressAgeSeconds.HasValue ? at.AddSeconds(-progressAgeSeconds.Value) : null
        };
        return SystemHealthServer.Derive(service, heartbeat, at);
    }

    internal static ServiceHealthReport Report(params ServiceHealth[] services) => new()
    {
        Services = [.. services],
        Overall = services.Max(s => s.State),
        WebVersion = "0.15.0",
        GeneratedAt = AsOf
    };
}
