// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Operations;
using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// The service health banner and the Schedule trigger chips appear on nearly every page, so they follow the viewer's
/// language (#3030). Both build their text from parts (which services are down, which days a Schedule runs), so these
/// pin that the parts assemble into grammatical sentences in each language, not English fragments in German words:
/// verb agreement with one service or several, and a time written the way the language writes times.
/// </summary>
[TestFixture]
public class LocalisedServiceHealthAndScheduleTests
{
    [Test]
    [SetUICulture("de")]
    [SetCulture("de")]
    public void Banner_German_SchedulerNeverReported_IsOneGermanSentencePair()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 2),
            ServiceHealthDisplayTests.Derive(JimService.WorkerDelivery, 2),
            SystemHealthServer.Derive(JimService.Scheduler, null, new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc)));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner!.Sentence, Is.EqualTo(
            "Der Scheduler hat sich noch nie gemeldet. Es wird nichts geplant; Arbeit in der Warteschlange bleibt erhalten " +
            "und wird fortgesetzt, sobald er wieder läuft."));
    }

    [Test]
    [SetUICulture("de")]
    [SetCulture("de")]
    public void Banner_German_WorkerAndSchedulerDown_AgreesInThePlural()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 4 * 60),
            ServiceHealthDisplayTests.Derive(JimService.WorkerDelivery, 4 * 60),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 3 * 60));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner!.Sentence, Is.EqualTo(
            "Der Worker und der Scheduler haben sich seit 4 Minuten nicht gemeldet. Es wird nichts synchronisiert, " +
            "zugestellt oder geplant; Arbeit in der Warteschlange bleibt erhalten und wird fortgesetzt, sobald sie wieder laufen."));
    }

    [Test]
    [SetUICulture("fr")]
    [SetCulture("fr")]
    public void Banner_French_WorkerDown_IsFrench()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 4 * 60),
            ServiceHealthDisplayTests.Derive(JimService.WorkerDelivery, 4 * 60),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner!.Sentence, Is.EqualTo(
            "Le Worker ne s’est pas signalé depuis 4 minutes. Rien n’est synchronisé ou livré ; le travail en file " +
            "d’attente est préservé et reprend dès qu’il revient."));
    }

    [Test]
    [SetUICulture("de")]
    [SetCulture("de")]
    public void Banner_German_StalledWorker_SaysSoInGerman()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 2, currentWork: "Full Import: HR", progressAgeSeconds: 20 * 60),
            ServiceHealthDisplayTests.Derive(JimService.WorkerDelivery, 2),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

        var banner = ServiceHealthDisplay.Banner(report);

        Assert.That(banner!.Sentence, Is.EqualTo("Der Worker hat bei Full Import: HR seit 20 Minuten keinen Fortschritt gemacht."));
    }

    [Test]
    [SetUICulture("en-US")]
    [SetCulture("en-US")]
    public void Banner_AmericanEnglish_SpellsSynchronizedTheAmericanWay()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 4 * 60),
            ServiceHealthDisplayTests.Derive(JimService.WorkerDelivery, 4 * 60),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

        Assert.That(ServiceHealthDisplay.Banner(report)!.Sentence, Does.Contain("Nothing is being synchronized or delivered"));
    }

    [TestCase(SchedulePatternType.SpecificTimes, null, "02:30", null, null, "Täglich um 02:30")]
    [TestCase(SchedulePatternType.SpecificTimes, "1,2,3,4,5", "09:00", null, null, "Werktags um 09:00")]
    [TestCase(SchedulePatternType.SpecificTimes, "1,2,3,4,5,6", "09:00", null, null, "Täglich außer So um 09:00")]
    [TestCase(SchedulePatternType.Interval, null, null, 1, ScheduleIntervalUnit.Hours, "Täglich, alle 1 Std.")]
    [SetUICulture("de")]
    [SetCulture("de")]
    public void Describe_German_WritesTheTriggerInGerman(
        SchedulePatternType pattern, string? days, string? runTimes, int? interval, ScheduleIntervalUnit? unit, string expected)
    {
        var schedule = new ScheduleHeader
        {
            Name = "Test",
            TriggerType = ScheduleTriggerType.Cron,
            PatternType = pattern,
            DaysOfWeek = days,
            RunTimes = runTimes,
            IntervalValue = interval,
            IntervalUnit = unit
        };

        Assert.That(ScheduleTriggerDisplay.Describe(schedule), Is.EqualTo(expected));
    }

    [Test]
    [SetUICulture("fr")]
    [SetCulture("fr")]
    public void Describe_French_ManualSchedule_IsFrench()
    {
        var schedule = new ScheduleHeader { Name = "Test", TriggerType = ScheduleTriggerType.Manual };

        Assert.That(ScheduleTriggerDisplay.Describe(schedule), Is.EqualTo("Manuel"));
    }

    [Test]
    [SetUICulture("fr")]
    [SetCulture("fr")]
    public void Describe_French_DailyAtTime_UsesTheTwentyFourHourClock()
    {
        var schedule = new ScheduleHeader
        {
            Name = "Test",
            TriggerType = ScheduleTriggerType.Cron,
            PatternType = SchedulePatternType.Custom,
            CronExpression = "30 14 * * *"
        };

        Assert.That(ScheduleTriggerDisplay.Describe(schedule), Is.EqualTo("Tous les jours à 14:30"));
    }
}
