// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Operations;
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
/// </summary>
public static class ServiceHealthDisplay
{
    /// <summary>
    /// The card title for a service. The two Worker services share a prefix because they share a process; an
    /// administrator looking for "the Worker" finds both cards at once.
    /// </summary>
    public static string Label(JimService service) => service switch
    {
        JimService.WorkerSync => "Worker · Sync",
        JimService.WorkerDelivery => "Worker · Passwords",
        JimService.Scheduler => "Scheduler",
        _ => service.ToString()
    };

    /// <summary>
    /// The status as the word on the card's pill.
    /// </summary>
    public static string StatusWord(ServiceHealthStatus status) => status switch
    {
        ServiceHealthStatus.Healthy => "Healthy",
        ServiceHealthStatus.Degraded => "Degraded",
        ServiceHealthStatus.Unhealthy => "Unhealthy",
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
    /// The suffix of the pill's <c>jim-service-health-pill--*</c> modifier class, which paints its dot and tint.
    /// </summary>
    public static string StatusModifier(ServiceHealthStatus status) => status switch
    {
        ServiceHealthStatus.Healthy => "healthy",
        ServiceHealthStatus.Degraded => "degraded",
        ServiceHealthStatus.Unhealthy => "unhealthy",
        _ => status.ToString().ToLowerInvariant()
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
            return "All services healthy";

        var parts = new List<string>();
        if (unhealthy > 0)
            parts.Add($"{unhealthy} {(unhealthy == 1 ? "service" : "services")} unhealthy");
        if (degraded > 0)
            parts.Add(parts.Count == 0 ? $"{degraded} {(degraded == 1 ? "service" : "services")} degraded" : $"{degraded} degraded");
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

        return string.IsNullOrWhiteSpace(service.CurrentWork) ? "Idle" : service.CurrentWork;
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

        return $"Was running: {service.CurrentWork}";
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
            { TotalMinutes: < 1 } => $"{(int)duration.TotalSeconds} s",
            { TotalHours: < 1 } => $"{(int)duration.TotalMinutes} min",
            { TotalDays: < 1 } => $"{(int)duration.TotalHours} h",
            _ => $"{(int)duration.TotalDays} d"
        };
    }

    /// <summary>
    /// A duration in its largest whole unit, in full words for a sentence ("4 minutes", "1 hour").
    /// </summary>
    public static string LongDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        return duration switch
        {
            { TotalMinutes: < 1 } => Unit((int)duration.TotalSeconds, "second"),
            { TotalHours: < 1 } => Unit((int)duration.TotalMinutes, "minute"),
            { TotalDays: < 1 } => Unit((int)duration.TotalHours, "hour"),
            _ => Unit((int)duration.TotalDays, "day")
        };

        static string Unit(int value, string singular) => value == 1 ? $"1 {singular}" : $"{value} {singular}s";
    }

    /// <summary>
    /// Whether the report warrants the Administration index's red dot: the same verdict as the banner's, so the dot
    /// and the banner appear and disappear together. That is any Unhealthy service, or a Degraded one whose
    /// condition is Stalled; a Degraded service whose heartbeat is merely overdue lights neither.
    /// </summary>
    public static bool NeedsAttention(ServiceHealthReport report) => Banner(report) != null;

    /// <summary>
    /// What the banner says, or null when it should not be shown. Shown for an outage (an Unhealthy service: no
    /// heartbeat, or never started) or a stalled task (Degraded by the Stalled condition); an overdue heartbeat is
    /// worth a glance on the strip, not an alarm on every page. Reads the report's verdict as it stands: which
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
            return OutageBanner(down, deliveryReported, report.GeneratedAt);

        var stalled = considered.Where(s => s.Condition == ServiceHealthCondition.Stalled).ToList();
        return stalled.Count > 0 ? StalledBanner(stalled, report.GeneratedAt) : null;
    }

    private static ServiceHealthBannerContent OutageBanner(List<ServiceHealth> down, bool deliveryReported, DateTime asOf)
    {
        var syncDown = down.Any(s => s.Service == JimService.WorkerSync);
        // With no delivery loop in the report, the synchronisation loop down is the whole Worker down.
        var passwordsDown = down.Any(s => s.Service == JimService.WorkerDelivery) || (syncDown && !deliveryReported);
        var schedulerDown = down.Any(s => s.Service == JimService.Scheduler);

        // Both Worker services down is the Worker down: name it once. One of them alone is named precisely, because
        // the other half of the process is still alive and the remedy is different.
        var names = new List<string>();
        if (syncDown && passwordsDown)
            names.Add("the Worker");
        else if (syncDown)
            names.Add("the Worker's synchronisation service");
        else if (passwordsDown)
            names.Add("the Worker's password delivery service");
        if (schedulerDown)
            names.Add("the Scheduler");

        var plural = names.Count > 1;
        var subject = Capitalise(JoinAnd(names));

        // "For how long" is the longest silence among the named services, because that is the one the administrator
        // is most behind on. A service that never started has no silence to measure; when none of them has, say so.
        var silences = down.Where(s => s.LastSeenAt.HasValue).Select(s => asOf - s.LastSeenAt!.Value).ToList();
        var first = silences.Count == 0
            ? $"{subject} {(plural ? "have" : "has")} never reported."
            : $"{subject} {(plural ? "have" : "has")} not reported for {LongDuration(silences.Max())}.";

        var stopped = new List<string>();
        if (syncDown)
            stopped.Add("synchronised");
        if (passwordsDown)
            stopped.Add("delivered");
        if (schedulerDown)
            stopped.Add("scheduled");
        var second = $"Nothing is being {JoinOr(stopped)}; queued work is safe and resumes when {(plural ? "they return" : "it returns")}.";

        return new ServiceHealthBannerContent(Severity.Error, $"{first} {second}");
    }

    private static ServiceHealthBannerContent StalledBanner(List<ServiceHealth> stalled, DateTime asOf)
    {
        // One sentence per stalled service. The sync loop is "the Worker" here because that is what an administrator
        // calls the thing running their Full Import; the other two are named for what they are.
        var sentences = stalled.Select(s =>
        {
            var name = s.Service switch
            {
                JimService.WorkerSync => "The Worker",
                JimService.WorkerDelivery => "The Worker's password delivery service",
                _ => "The Scheduler"
            };
            var since = s.LastProgressAt is { } lastProgressAt ? $" for {LongDuration(asOf - lastProgressAt)}" : string.Empty;
            return $"{name} has made no progress on {s.CurrentWork}{since}.";
        });

        return new ServiceHealthBannerContent(Severity.Warning, string.Join(" ", sentences));
    }

    private static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}"
    };

    private static string JoinOr(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} or {items[^1]}"
    };

    private static string Capitalise(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>
/// What the banner shows: which alert severity, and the one or two sentences inside it.
/// </summary>
/// <param name="Severity">Error for an outage, Warning for a stalled task.</param>
/// <param name="Sentence">The text, already complete; the banner adds only its links.</param>
public sealed record ServiceHealthBannerContent(Severity Severity, string Sentence);
