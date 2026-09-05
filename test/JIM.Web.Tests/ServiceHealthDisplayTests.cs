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
    [TestCase(JimService.WorkerDelivery, "Worker · Passwords")]
    [TestCase(JimService.Scheduler, "Scheduler")]
    public void Label_EachService_IsTheCardTitle(JimService service, string expected)
    {
        Assert.That(ServiceHealthDisplay.Label(service), Is.EqualTo(expected));
    }

    [TestCase(ServiceHealthStatus.Healthy, "Healthy", Color.Success, "healthy")]
    [TestCase(ServiceHealthStatus.Degraded, "Degraded", Color.Warning, "degraded")]
    [TestCase(ServiceHealthStatus.Unhealthy, "Unhealthy", Color.Error, "unhealthy")]
    public void Status_EachValue_HasWordColourAndModifier(ServiceHealthStatus status, string word, Color colour, string modifier)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ServiceHealthDisplay.StatusWord(status), Is.EqualTo(word));
            Assert.That(ServiceHealthDisplay.StatusColour(status), Is.EqualTo(colour));
            Assert.That(ServiceHealthDisplay.StatusModifier(status), Is.EqualTo(modifier));
        }
    }

    [Test]
    public void Summary_EveryServiceHealthy_SaysSo()
    {
        var report = Report(Derive(JimService.WorkerSync, 2), Derive(JimService.Scheduler, 2));

        Assert.That(ServiceHealthDisplay.Summary(report), Is.EqualTo("All services healthy"));
    }

    [Test]
    public void Summary_OneDegraded_CountsIt()
    {
        var report = Report(Derive(JimService.WorkerSync, 30), Derive(JimService.Scheduler, 2));

        Assert.That(ServiceHealthDisplay.Summary(report), Is.EqualTo("1 service degraded"));
    }

    [Test]
    public void Summary_TwoUnhealthy_SpeaksInThePlural()
    {
        var report = Report(Derive(JimService.WorkerSync, 4 * 60), Derive(JimService.Scheduler, 3 * 60));

        Assert.That(ServiceHealthDisplay.Summary(report), Is.EqualTo("2 services unhealthy"));
    }

    [Test]
    public void Summary_UnhealthyAndDegraded_WorstFirst()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 30),
            Derive(JimService.WorkerDelivery, 2),
            Derive(JimService.Scheduler, 3 * 60));

        Assert.That(ServiceHealthDisplay.Summary(report), Is.EqualTo("1 service unhealthy, 1 degraded"));
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
    public void Activity_ServiceWithCurrentWork_IsTheWork()
    {
        var service = Derive(JimService.WorkerSync, ageSeconds: 2, currentWork: "Full Import: Corporate Directory");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ServiceHealthDisplay.Activity(service), Is.EqualTo("Full Import: Corporate Directory"));
            Assert.That(ServiceHealthDisplay.WasDoing(service), Is.Null, "a live service's work is its activity, not its history");
        }
    }

    [Test]
    public void Activity_HealthyServiceWithoutWork_IsIdle()
    {
        var service = Derive(JimService.Scheduler, ageSeconds: 2);

        Assert.That(ServiceHealthDisplay.Activity(service), Is.EqualTo("Idle"));
    }

    [Test]
    public void Activity_DegradedServiceWithWork_IsStillTheWork()
    {
        // Degraded by an overdue heartbeat: the process is alive, so what it says it is doing still stands.
        var service = Derive(JimService.WorkerSync, ageSeconds: 30, currentWork: "Full Import: Corporate Directory");

        Assert.That(ServiceHealthDisplay.Activity(service), Is.EqualTo("Full Import: Corporate Directory"));
    }

    [Test]
    public void Activity_UnhealthyService_IsTheReasonAndItsWorkMovesToWasDoing()
    {
        var service = Derive(JimService.WorkerSync, ageSeconds: 4 * 60, currentWork: "Full Import: Corporate Directory");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ServiceHealthDisplay.Activity(service), Is.EqualTo("No heartbeat for 4 minutes"),
                "a dead process's last words about its work are not what it is doing now");
            Assert.That(ServiceHealthDisplay.WasDoing(service), Is.EqualTo("Was running: Full Import: Corporate Directory"));
        }
    }

    [Test]
    public void Activity_UnhealthyIdleService_HasNothingItWasDoing()
    {
        var service = Derive(JimService.Scheduler, ageSeconds: 3 * 60);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ServiceHealthDisplay.Activity(service), Is.EqualTo("No heartbeat for 3 minutes"));
            Assert.That(ServiceHealthDisplay.WasDoing(service), Is.Null);
        }
    }

    /// <summary>
    /// The condition slot is never left blank on a card that has anything to say. A live service's condition is
    /// its reason; an unhealthy one has put its reason on the activity line already, so the slot carries what it
    /// was running (the more useful fact, because it names the work an administrator may need to restart) and,
    /// failing that, when it was last heard from.
    /// </summary>
    [Test]
    public void Condition_UnhealthyServiceWithWork_IsWhatItWasRunning()
    {
        var service = Derive(JimService.WorkerSync, ageSeconds: 4 * 60, currentWork: "Full Import: Corporate Directory");

        Assert.That(ServiceHealthDisplay.Condition(service), Is.EqualTo("Was running: Full Import: Corporate Directory"));
    }

    [Test]
    public void Condition_UnhealthyIdleService_IsItsLastHeartbeat()
    {
        var service = Derive(JimService.Scheduler, ageSeconds: 3 * 60);
        var lastSeen = service.LastSeenAt!.Value.ToLocalTime().ToFriendlyDate();

        Assert.That(ServiceHealthDisplay.Condition(service), Is.EqualTo($"Last heartbeat {lastSeen}"));
    }

    [Test]
    public void Condition_LiveService_IsItsReason()
    {
        var service = Derive(JimService.WorkerSync, ageSeconds: 30);

        Assert.That(ServiceHealthDisplay.Condition(service), Is.EqualTo(service.Reason));
    }

    [Test]
    public void Condition_NeverStartedService_HasNothingToSay()
    {
        var service = SystemHealthServer.Derive(JimService.WorkerDelivery, null, AsOf);

        Assert.That(ServiceHealthDisplay.Condition(service), Is.Null);
    }

    [Test]
    public void Activity_NeverStartedService_SaysSo()
    {
        var service = SystemHealthServer.Derive(JimService.WorkerDelivery, null, AsOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ServiceHealthDisplay.Activity(service), Is.EqualTo("Never started"));
            Assert.That(ServiceHealthDisplay.WasDoing(service), Is.Null);
        }
    }

    [Test]
    public void Banner_EveryServiceHealthy_IsNothing()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 2),
            Derive(JimService.WorkerDelivery, 2),
            Derive(JimService.Scheduler, 2));

        Assert.That(ServiceHealthDisplay.Banner(report), Is.Null);
    }

    [Test]
    public void Banner_OverdueHeartbeat_IsNothing()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 30),
            Derive(JimService.WorkerDelivery, 2),
            Derive(JimService.Scheduler, 2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ServiceHealthDisplay.Banner(report), Is.Null, "a few late heartbeats are worth a glance, not a banner on every page");
            Assert.That(ServiceHealthDisplay.NeedsAttention(report), Is.False, "nor a red dot on the Administration index");
        }
    }

    [Test]
    public void Banner_BothWorkerServicesWithNoHeartbeat_NamesTheWorkerOnce()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 4 * 60),
            Derive(JimService.WorkerDelivery, 4 * 60),
            Derive(JimService.Scheduler, 2));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(banner!.Severity, Is.EqualTo(Severity.Error));
            Assert.That(banner.Sentence, Is.EqualTo(
                "The Worker has not reported for 4 minutes. Nothing is being synchronised or delivered; queued work is safe and resumes when it returns."));
            Assert.That(ServiceHealthDisplay.NeedsAttention(report), Is.True);
        }
    }

    [Test]
    public void Banner_WorkerSyncWithNoHeartbeatAndPasswordDeliveryNeverStarted_NamesTheWorkerOnce()
    {
        // The two services share a process: a Worker that is down takes both with it, and an administrator wants
        // to be told once that the Worker is gone, not twice in different words.
        var report = Report(
            Derive(JimService.WorkerSync, 4 * 60),
            SystemHealthServer.Derive(JimService.WorkerDelivery, null, AsOf),
            Derive(JimService.Scheduler, 2));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        Assert.That(banner!.Sentence, Does.StartWith("The Worker has not reported for 4 minutes."));
    }

    [Test]
    public void Banner_PasswordDeliveryThatStoppedReporting_IsAnOutage()
    {
        // Distinct from never started: this loop did exist and has gone quiet while its sibling is still alive.
        var report = Report(
            Derive(JimService.WorkerSync, 2),
            Derive(JimService.WorkerDelivery, 4 * 60),
            Derive(JimService.Scheduler, 2));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        Assert.That(banner!.Sentence, Is.EqualTo(
            "The Worker's password delivery service has not reported for 4 minutes. Nothing is being delivered; queued work is safe and resumes when it returns."));
    }

    [Test]
    public void Banner_SchedulerWithNoHeartbeat_NamesTheScheduler()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 2),
            Derive(JimService.WorkerDelivery, 2),
            Derive(JimService.Scheduler, 3 * 60));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        Assert.That(banner!.Sentence, Is.EqualTo(
            "The Scheduler has not reported for 3 minutes. Nothing is being scheduled; queued work is safe and resumes when it returns."));
    }

    [Test]
    public void Banner_WorkerAndSchedulerWithNoHeartbeat_NamesBothAndSpeaksInThePlural()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 4 * 60),
            Derive(JimService.WorkerDelivery, 4 * 60),
            Derive(JimService.Scheduler, 3 * 60));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        Assert.That(banner!.Sentence, Is.EqualTo(
            "The Worker and the Scheduler have not reported for 4 minutes. Nothing is being synchronised, delivered or scheduled; queued work is safe and resumes when they return."));
    }

    [Test]
    public void Banner_EverythingNeverStarted_SaysNeverRatherThanForZeroSeconds()
    {
        var report = Report(
            SystemHealthServer.Derive(JimService.WorkerSync, null, AsOf),
            SystemHealthServer.Derive(JimService.WorkerDelivery, null, AsOf),
            SystemHealthServer.Derive(JimService.Scheduler, null, AsOf));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        Assert.That(banner!.Sentence, Does.StartWith("The Worker and the Scheduler have never reported."));
    }

    [Test]
    public void Banner_WorkerStalled_NamesTheWorkAndHowLong()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 2, currentWork: "Full Import: Corporate Directory", progressAgeSeconds: 12 * 60),
            Derive(JimService.WorkerDelivery, 2),
            Derive(JimService.Scheduler, 2));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(banner!.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(banner.Sentence, Is.EqualTo("The Worker has made no progress on Full Import: Corporate Directory for 12 minutes."));
            Assert.That(ServiceHealthDisplay.NeedsAttention(report), Is.True, "a stalled task earns the red dot; an overdue heartbeat does not");
        }
    }

    [Test]
    public void Banner_StalledBesideAnOverdueHeartbeat_NamesOnlyTheStalledOne()
    {
        // Both are Degraded; only the stalled condition is worth a banner, and the overdue Scheduler is not named.
        var report = Report(
            Derive(JimService.WorkerSync, 2, currentWork: "Full Import: Corporate Directory", progressAgeSeconds: 12 * 60),
            Derive(JimService.Scheduler, 30));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner, Is.Not.Null);
        Assert.That(banner!.Sentence, Is.EqualTo("The Worker has made no progress on Full Import: Corporate Directory for 12 minutes."));
    }

    [Test]
    public void Banner_NoHeartbeatOutranksStalled()
    {
        var report = Report(
            Derive(JimService.WorkerSync, 2, currentWork: "Full Import: Corporate Directory", progressAgeSeconds: 12 * 60),
            Derive(JimService.WorkerDelivery, 2),
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
                "a service that has never started has no version to disagree with");
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
        Overall = services.Max(s => s.Status),
        WebVersion = "0.15.0",
        GeneratedAt = AsOf
    };
}
