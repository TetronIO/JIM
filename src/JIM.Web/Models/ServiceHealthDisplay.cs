// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Services;
using JIM.Models.Operations;
using JIM.Models.Scheduling.DTOs;
using JIM.Web.Localisation;
using JIM.Web.Resources;
using MudBlazor;

namespace JIM.Web.Models;

/// <summary>
/// How a service's health reads on screen: the card titles, status words and summary line on the Operations strip,
/// the sentence in the banner administrators see on every page, and the verdict behind the red dot on the
/// Administration index.
/// <para>
/// One class because the three surfaces describe the same report and must agree on it. The banner in particular
/// has to decide which services to name and how, and that decision belongs beside the words it produces rather
/// than in a layout component.
/// </para>
/// <para>
/// Every word comes from <c>HealthText</c> in the viewer's language (#3030). The banner names which services are down,
/// so its sentences are whole templates in singular and plural forms with the services filled in, never English
/// grammar assembled from translated fragments: a German subject takes "hat" or "haben", and a duration after
/// "seit" takes the dative.
/// </para>
/// </summary>
public static class ServiceHealthDisplay
{
    /// <summary>
    /// The card title for a service. The Worker's services share a prefix because they share a process; an
    /// administrator looking for "the Worker" finds every one of its cards at once.
    /// </summary>
    public static string Label(JimService service) => service switch
    {
        JimService.WorkerSync => HealthText.LabelWorkerSync,
        JimService.WorkerDelivery => HealthText.LabelWorkerPasswords,
        JimService.WorkerExchange => HealthText.LabelWorkerExchange,
        JimService.Scheduler => HealthText.LabelScheduler,
        _ => service.ToString()
    };

    /// <summary>
    /// The status as the word on the card's pill.
    /// </summary>
    public static string StatusWord(ServiceHealthStatus status) => status switch
    {
        ServiceHealthStatus.Healthy => HealthText.StatusHealthy,
        ServiceHealthStatus.Degraded => HealthText.StatusDegraded,
        ServiceHealthStatus.Unhealthy => HealthText.StatusUnhealthy,
        _ => status.ToString()
    };

    /// <summary>
    /// The colour of the status pill: green, amber, red. The pill is the only coloured element on a card, so this is
    /// the one place a status becomes a colour.
    /// </summary>
    public static Color StatusColour(ServiceHealthStatus status) => status switch
    {
        ServiceHealthStatus.Healthy => Color.Success,
        ServiceHealthStatus.Degraded => Color.Warning,
        ServiceHealthStatus.Unhealthy => Color.Error,
        _ => Color.Default
    };

    /// <summary>
    /// The suffix of the pill's <c>jim-status-pill--*</c> modifier class, which paints its dot and tint. The
    /// vocabulary (ok, warn, err, neutral) is shared with every other status pill in the portal (the password
    /// history's per-system chips among them), so one rule in site.css paints them all and a health status is
    /// translated into it here rather than lending the shared rule its own names.
    /// </summary>
    public static string StatusModifier(ServiceHealthStatus status) => status switch
    {
        ServiceHealthStatus.Healthy => "ok",
        ServiceHealthStatus.Degraded => "warn",
        ServiceHealthStatus.Unhealthy => "err",
        _ => "neutral"
    };

    /// <summary>
    /// The header's one-line summary of the report: "All services healthy", or a count per status that is not,
    /// worst first ("1 service unhealthy, 1 degraded"). The noun is pluralised by the first count only, because the
    /// rest read as a continuation of it.
    /// </summary>
    public static string Summary(ServiceHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var unhealthy = report.Services.Count(s => s.Status == ServiceHealthStatus.Unhealthy);
        var degraded = report.Services.Count(s => s.Status == ServiceHealthStatus.Degraded);
        if (unhealthy == 0 && degraded == 0)
            return HealthText.SummaryAllHealthy;

        var parts = new List<string>();
        if (unhealthy > 0)
            parts.Add(Counted(unhealthy, HealthText.SummaryUnhealthyOne, HealthText.SummaryUnhealthyOther));
        if (degraded > 0)
        {
            parts.Add(parts.Count == 0
                ? Counted(degraded, HealthText.SummaryDegradedOne, HealthText.SummaryDegradedOther)
                : Counted(degraded, HealthText.SummaryDegradedContinuationOne, HealthText.SummaryDegradedContinuationOther));
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// The card's activity line: what the service is doing. The work in hand, "Idle" when there is none, or for an
    /// unhealthy service the reason itself ("No heartbeat for 4 minutes", "Never started"), because a dead
    /// process's last words about its work are not what it is doing now; see <see cref="WasDoing"/> for those.
    /// </summary>
    public static string Activity(ServiceHealth service)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (service.Status == ServiceHealthStatus.Unhealthy)
            return service.Reason;

        return string.IsNullOrWhiteSpace(service.CurrentWork) ? HealthText.Idle : service.CurrentWork;
    }

    /// <summary>
    /// For an unhealthy service that reported work before it went quiet, what that work was ("Was running: Full
    /// Import: Corporate Directory"); null for a healthy or degraded service (its work is on the activity line) and
    /// for one that was idle or never started.
    /// </summary>
    public static string? WasDoing(ServiceHealth service)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (service.Status != ServiceHealthStatus.Unhealthy || string.IsNullOrWhiteSpace(service.CurrentWork))
            return null;

        return Format(HealthText.WasRunning, service.CurrentWork);
    }

    /// <summary>
    /// The card's condition line. For a healthy or degraded service, its reason in plain words. An unhealthy
    /// service has already put its reason on the activity line, so this slot carries the next most useful fact:
    /// the work it was running when it went quiet (see <see cref="WasDoing"/>), or failing that when it was last
    /// heard from, in the viewer's local time. Null only for a service that never started, which has neither.
    /// </summary>
    public static string? Condition(ServiceHealth service)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (service.Status != ServiceHealthStatus.Unhealthy)
            return service.Reason;

        if (WasDoing(service) is { } wasDoing)
            return wasDoing;

        return service.LastSeenAt is { } lastSeenAt
            ? Format(HealthText.LastHeartbeat, lastSeenAt.ToLocalTime().ToFriendlyDate())
            : null;
    }

    /// <summary>
    /// The card's condition line with the uptime after it ("Heartbeat 3 seconds ago · up 54 s"), or null for a
    /// service that never started. The uptime rides on this line rather than on a footer of its own because it is
    /// the same kind of fact as the heartbeat: how the process is doing, not which process it is.
    /// </summary>
    public static string? ConditionLine(ServiceHealth service, DateTime asOf)
    {
        var condition = Condition(service);
        var uptime = Uptime(service, asOf);
        if (uptime == null)
            return condition;

        var up = Format(HealthText.Up, CompactDuration(uptime.Value));
        return condition == null ? up : $"{condition} · {up}";
    }

    /// <summary>
    /// How long the reporting instance has been up: to the report's own time for a service still reporting, and to
    /// its last heartbeat for one that has gone quiet, because that figure says how long it ran before it stopped
    /// and would otherwise keep growing after death. Null for a service that never started.
    /// </summary>
    public static TimeSpan? Uptime(ServiceHealth service, DateTime asOf)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (service.StartedAt is not { } startedAt)
            return null;

        var until = service.Status == ServiceHealthStatus.Unhealthy && service.LastSeenAt is { } lastSeenAt ? lastSeenAt : asOf;
        return until - startedAt;
    }

    /// <summary>
    /// The instance id where it says more than the host does (it is the host plus a per-process suffix), or null
    /// where it is the host name over again or the service never reported.
    /// </summary>
    public static string? Instance(ServiceHealth service)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (service.InstanceId == null || string.Equals(service.InstanceId, service.HostName, StringComparison.Ordinal))
            return null;

        return service.InstanceId;
    }

    /// <summary>
    /// Whether the service reports a version other than the web tier's. Only judged when the service has reported
    /// one at all; a service never seen has no version to disagree with.
    /// </summary>
    public static bool HasVersionSkew(ServiceHealth service, string webVersion)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.Version != null && !string.Equals(service.Version, webVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// A duration in its largest whole unit, abbreviated for a card ("40 s", "12 min", "5 h", "3 d"). Rounded down
    /// and never negative, so a clock a little ahead of the database reads "0 s" rather than "-2 s".
    /// </summary>
    public static string CompactDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        return duration switch
        {
            { TotalMinutes: < 1 } => Format(HealthText.CompactSeconds, (int)duration.TotalSeconds),
            { TotalHours: < 1 } => Format(HealthText.CompactMinutes, (int)duration.TotalMinutes),
            { TotalDays: < 1 } => Format(HealthText.CompactHours, (int)duration.TotalHours),
            _ => Format(HealthText.CompactDays, (int)duration.TotalDays)
        };
    }

    /// <summary>
    /// When the next Schedule fires, in the reader's terms (#2549): "today 16:30", "tomorrow 02:00", the weekday within
    /// the week ahead ("Wed 02:00"), and the date beyond it ("20 Sep 02:00").
    /// </summary>
    /// <param name="nextRunUtc">When the Schedule is next due (UTC).</param>
    /// <param name="asOfUtc">Now (UTC): the report's own time, so the line agrees with the rest of the card.</param>
    /// <param name="zone">The time zone to say it in.</param>
    public static string NextScheduleWhen(DateTime nextRunUtc, DateTime asOfUtc, TimeZoneInfo zone)
    {
        var next = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nextRunUtc, DateTimeKind.Utc), zone);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(asOfUtc, DateTimeKind.Utc), zone).Date;
        var days = (next.Date - today).Days;
        var time = next.ToString("HH:mm", CultureInfo.InvariantCulture);

        // The weekday and month are the viewer's language's own abbreviations ("Wed", "Mi", "mer.").
        return days switch
        {
            <= 0 => Format(HealthText.NextToday, time),
            1 => Format(HealthText.NextTomorrow, time),
            < 7 => $"{next.ToString("ddd", CultureInfo.CurrentCulture)} {time}",
            _ => $"{next.ToString("d MMM", CultureInfo.CurrentCulture)} {time}"
        };
    }

    /// <summary>How long until the next Schedule fires, abbreviated for a card ("in 3 d", "in 40 min").</summary>
    public static string NextScheduleCountdown(DateTime nextRunUtc, DateTime asOfUtc) =>
        Format(HealthText.NextCountdown, CompactDuration(nextRunUtc - asOfUtc));

    /// <summary>
    /// The phrase the collapsed Service Health header adds, so the next Schedule is never hidden with the cards
    /// ("next Schedule tomorrow 02:00"), or null when no Schedule is due.
    /// </summary>
    public static string? NextScheduleHeaderPhrase(NextDueSchedule? next, DateTime asOfUtc, TimeZoneInfo zone) =>
        next == null ? null : Format(HealthText.NextScheduleHeader, NextScheduleWhen(next.NextRunTime, asOfUtc, zone));

    /// <summary>
    /// A duration in its largest whole unit, in full words for a sentence ("4 minutes", "1 hour"). The units are the
    /// relative-time ones, which German writes in the dative: right after "seit", where the banner puts them.
    /// </summary>
    public static string LongDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        return duration switch
        {
            { TotalMinutes: < 1 } => Counted((int)duration.TotalSeconds, TimeText.RelativeSecondOne, TimeText.RelativeSecondOther),
            { TotalHours: < 1 } => Counted((int)duration.TotalMinutes, TimeText.RelativeMinuteOne, TimeText.RelativeMinuteOther),
            { TotalDays: < 1 } => Counted((int)duration.TotalHours, TimeText.RelativeHourOne, TimeText.RelativeHourOther),
            _ => Counted((int)duration.TotalDays, TimeText.RelativeDayOne, TimeText.RelativeDayOther)
        };
    }

    /// <summary>
    /// Whether the report warrants the Administration index's red dot: the same verdict as the banner's, so the dot
    /// and the banner appear and disappear together. That is any Unhealthy service, or a Degraded one whose
    /// condition is Stalled; a Degraded service whose heartbeat is merely overdue lights neither.
    /// </summary>
    public static bool NeedsAttention(ServiceHealthReport report) => Banner(report) != null;

    /// <summary>
    /// What the banner says, or null when it should not be shown. Shown for an outage (an Unhealthy service: no
    /// heartbeat, or never started), a stalled task (Degraded by the Stalled condition) or low memory (Degraded by
    /// MemoryLow), in that order of precedence; an overdue heartbeat is worth a glance on the strip, not an alarm on
    /// every page. Reads the report's verdict as it stands: which
    /// services a deployment is expected to run is the read model's decision
    /// (<c>SystemHealthServer.ExpectedServices</c>), so a service reported as never started is one that should
    /// have been.
    /// </summary>
    public static ServiceHealthBannerContent? Banner(ServiceHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var considered = report.Services;
        if (considered.Count == 0)
            return null;

        // Until the Worker reports a separate password delivery loop, the synchronisation loop is simply "the
        // Worker", and a Worker that is down delivers nothing either; the finer naming below only earns its place
        // once both loops are in the report.
        var deliveryReported = considered.Any(s => s.Service == JimService.WorkerDelivery);

        var down = considered.Where(s => s.Status == ServiceHealthStatus.Unhealthy).ToList();
        if (down.Count > 0)
            return OutageBanner(down, deliveryReported, considered.Select(s => s.Service).ToHashSet(), report.GeneratedAt);

        var stalled = considered.Where(s => s.Condition == ServiceHealthCondition.Stalled).ToList();
        if (stalled.Count > 0)
            return StalledBanner(stalled, report.GeneratedAt);

        var memoryLow = considered.Where(s => s.Condition == ServiceHealthCondition.MemoryLow).ToList();
        return memoryLow.Count > 0 ? MemoryLowBanner(memoryLow) : null;
    }

    /// <summary>
    /// The Memory row in a card's details: what the process holds and how full the memory it draws on is
    /// ("41 GB held · 66% of 62 GB in use"), short enough for a card three across. Null for a service that did not
    /// report memory.
    /// </summary>
    public static string? Memory(ServiceHealth service)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (ReadingOf(service) is not { TotalBytes: { } total, UsedFraction: { } share } reading)
            return null;

        return Format(HealthText.MemoryDetail, MemoryPressure.Bytes(reading.ProcessBytes), MemoryPressure.Percent(share), MemoryPressure.Bytes(total));
    }

    /// <summary>
    /// A warning when a service is short of memory, before it is stopped part-way through its work. The Worker's
    /// services share one process, so they are named once as the Worker, with the synchronisation loop's figures;
    /// the Worker's own remedy (it stops its running work at <see cref="MemoryPressure.StopShare"/>) is said only
    /// when the Worker is the one short.
    /// </summary>
    private static ServiceHealthBannerContent MemoryLowBanner(List<ServiceHealth> memoryLow)
    {
        var sentences = new List<string>();

        var worker = memoryLow.FirstOrDefault(s => s.Service == JimService.WorkerSync)
                     ?? memoryLow.FirstOrDefault(s => s.Service == JimService.WorkerDelivery)
                     ?? memoryLow.FirstOrDefault(s => s.Service == JimService.WorkerExchange);
        if (worker != null && ReadingOf(worker) is { } workerReading)
        {
            sentences.Add(MemoryLowSentence(HealthText.MemoryLowWorker, workerReading));
            sentences.Add(Format(HealthText.MemoryWorkerRemedy, MemoryPressure.Percent(MemoryPressure.StopShare)));
        }

        var scheduler = memoryLow.FirstOrDefault(s => s.Service == JimService.Scheduler);
        if (scheduler != null && ReadingOf(scheduler) is { } schedulerReading)
        {
            sentences.Add(MemoryLowSentence(HealthText.MemoryLowScheduler, schedulerReading));
            if (worker == null)
                sentences.Add(HealthText.MemoryRemedy);
        }

        return new ServiceHealthBannerContent(Severity.Warning, string.Join(" ", sentences));
    }

    /// <summary>
    /// "Memory is low for the Worker: 57 GB of 62 GB in use, 41 GB of it by the Worker." The same figures as
    /// <see cref="MemoryPressure.Describe"/>, which writes English for Activity messages, in the viewer's language.
    /// </summary>
    private static string MemoryLowSentence(string template, MemoryReading reading) =>
        Format(template, MemoryPressure.Bytes(reading.UsedBytes ?? 0), MemoryPressure.Bytes(reading.TotalBytes ?? 0),
            MemoryPressure.Bytes(reading.ProcessBytes));

    private static MemoryReading? ReadingOf(ServiceHealth service) =>
        service is { ProcessMemoryBytes: { } process, MemoryTotalBytes: { } total, MemoryAvailableBytes: { } available }
            ? new MemoryReading { ProcessBytes = process, TotalBytes = total, AvailableBytes = available }
            : null;


    private static ServiceHealthBannerContent OutageBanner(List<ServiceHealth> down, bool deliveryReported, IReadOnlySet<JimService> reportedServices, DateTime asOf)
    {
        var syncDown = down.Any(s => s.Service == JimService.WorkerSync);
        // With no delivery loop in the report, the synchronisation loop down is the whole Worker down.
        var passwordsDown = down.Any(s => s.Service == JimService.WorkerDelivery) || (syncDown && !deliveryReported);
        // As with password delivery: an Exchange delivery loop (#2021) the report does not include is no part of whether
        // the whole Worker is down.
        var exchangeReported = reportedServices.Contains(JimService.WorkerExchange);
        var exchangeDown = down.Any(s => s.Service == JimService.WorkerExchange);
        var schedulerDown = down.Any(s => s.Service == JimService.Scheduler);

        // Every Worker service down is the Worker down: name it once. Any of them alone is named precisely, because
        // the rest of the process is still alive and the remedy is different.
        var names = new List<string>();
        if (syncDown && passwordsDown && (exchangeDown || !exchangeReported))
        {
            names.Add(HealthText.NameWorker);
        }
        else
        {
            if (syncDown)
                names.Add(HealthText.NameWorkerSync);
            if (passwordsDown)
                names.Add(HealthText.NameWorkerPasswords);
            if (exchangeDown)
                names.Add(HealthText.NameWorkerExchange);
        }

        if (schedulerDown)
            names.Add(HealthText.NameScheduler);

        var plural = names.Count > 1;
        var subject = Capitalise(JoinList(names, HealthText.ListAnd));

        // "For how long" is the longest silence among the named services, because that is the one the administrator
        // is most behind on. A service that never started has no silence to measure; when none of them has, say so.
        var silences = down.Where(s => s.LastSeenAt.HasValue).Select(s => asOf - s.LastSeenAt!.Value).ToList();
        var first = silences.Count == 0
            ? Format(plural ? HealthText.NeverReportedOther : HealthText.NeverReportedOne, subject)
            : Format(plural ? HealthText.NotReportedOther : HealthText.NotReportedOne, subject, LongDuration(silences.Max()));

        var stopped = new List<string>();
        if (syncDown)
            stopped.Add(HealthText.StoppedSynchronised);
        if (passwordsDown || exchangeDown)
            stopped.Add(HealthText.StoppedDelivered);
        if (schedulerDown)
            stopped.Add(HealthText.StoppedScheduled);
        var second = Format(plural ? HealthText.NothingBeingOther : HealthText.NothingBeingOne, JoinList(stopped, HealthText.ListOr));

        return new ServiceHealthBannerContent(Severity.Error, $"{first} {second}");
    }

    private static ServiceHealthBannerContent StalledBanner(List<ServiceHealth> stalled, DateTime asOf)
    {
        // One sentence per stalled service. The sync loop is "the Worker" here because that is what an administrator
        // calls the thing running their Full Import; the other two are named for what they are.
        var sentences = stalled.Select(s =>
        {
            var name = Capitalise(s.Service switch
            {
                JimService.WorkerSync => HealthText.NameWorker,
                JimService.WorkerDelivery => HealthText.NameWorkerPasswords,
                JimService.WorkerExchange => HealthText.NameWorkerExchange,
                _ => HealthText.NameScheduler
            });
            return s.LastProgressAt is { } lastProgressAt
                ? Format(HealthText.StalledFor, name, s.CurrentWork ?? string.Empty, LongDuration(asOf - lastProgressAt))
                : Format(HealthText.Stalled, name, s.CurrentWork ?? string.Empty);
        });

        return new ServiceHealthBannerContent(Severity.Warning, string.Join(" ", sentences));
    }

    /// <summary>
    /// "a, b and c": the items before the last separated by commas, the last joined by <paramref name="lastJoin"/>
    /// (<c>HealthText.ListAnd</c> or <c>ListOr</c>), whose word for "and" or "or" is the language's own.
    /// </summary>
    private static string JoinList(IReadOnlyList<string> items, string lastJoin) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => Format(lastJoin, string.Join(", ", items.Take(items.Count - 1)), items[^1])
    };

    private static string Capitalise(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];

    private static string Counted(int count, string one, string other) => Format(PluralRules.Choose(count, one, other), count);

    private static string Format(string template, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, template, arguments);
}

/// <summary>
/// What the banner shows: which alert severity, and the one or two sentences inside it.
/// </summary>
/// <param name="Severity">Error for an outage, Warning for a stalled task.</param>
/// <param name="Sentence">The text, already complete; the banner adds only its links.</param>
public sealed record ServiceHealthBannerContent(Severity Severity, string Sentence);
