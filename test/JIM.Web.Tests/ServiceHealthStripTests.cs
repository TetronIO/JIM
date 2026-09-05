// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using AngleSharp.Dom;
using Bunit;
using JIM.Application.Servers;
using JIM.Models.Operations;
using JIM.Models.Tasking;
using JIM.Web.Services;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The panel at the top of Operations: a header that summarises the report and shows whether live updates are
/// connected, and one identical card per background service. What these pin is what an administrator reads off
/// the panel at a glance: the summary sentence, the status word on each card's pill, the activity and condition
/// lines, and the chip that says the Worker was not upgraded with the portal.
/// </summary>
[TestFixture]
public class ServiceHealthStripTests : JimComponentTestContext
{
    private FakeUiNotificationService _notifications = null!;

    protected override void ConfigureAdditionalServices()
    {
        _notifications = new FakeUiNotificationService();
        Services.AddSingleton<IUiNotificationService>(_notifications);
    }

    [SetUp]
    public void SetUp() => _notifications.IsRealTimeAvailable = true;

    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    private static ServiceHealthReport HealthyReport() => ServiceHealthDisplayTests.Report(
        ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 2),
        ServiceHealthDisplayTests.Derive(JimService.WorkerDelivery, 2),
        ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

    private IRenderedComponent<ServiceHealthStrip> RenderStrip(ServiceHealthReport report) =>
        Render<ServiceHealthStrip>(p => p.Add(c => c.Report, report));

    private static IElement Card(IRenderedComponent<ServiceHealthStrip> cut, JimService service) =>
        cut.Find($".jim-service-health-card[data-service='{service}']");

    private static string Text(IElement card, string selector) => card.QuerySelector(selector)!.TextContent.Trim();

    private static IElement LiveIndicator(IRenderedComponent<ServiceHealthStrip> cut) => cut.Find("[data-testid='live-updates']");

    [Test]
    public void ServiceHealthStrip_Report_RendersTheHeaderAndOneCardPerService()
    {
        var cut = RenderStrip(HealthyReport());

        var cards = cut.FindAll(".jim-service-health-card");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(".jim-service-health-title").TextContent.Trim(), Is.EqualTo("Service Health"));
            Assert.That(cut.Find(".jim-service-health-summary").TextContent.Trim(), Is.EqualTo("All services healthy"));
            Assert.That(cards, Has.Count.EqualTo(3), "live updates is a header indicator, not a card");
            Assert.That(cards.Select(c => c.QuerySelector(".jim-service-health-label")!.TextContent.Trim()),
                Is.EqualTo(new[] { "Worker · Sync", "Worker · Passwords", "Scheduler" }));
        }
    }

    [Test]
    public void ServiceHealthStrip_ReportWithProblems_SummarisesWorstFirst()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 30),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 3 * 60));

        var cut = RenderStrip(report);

        Assert.That(cut.Find(".jim-service-health-summary").TextContent.Trim(), Is.EqualTo("1 service unhealthy, 1 degraded"));
    }

    [Test]
    public void ServiceHealthStrip_EveryCard_HasTheSameFourSlotsInTheSameOrder()
    {
        // One structure for every card is what lets the eye find the same fact in the same place, and what keeps
        // the cards one height. The never-started card has the least to say and must still carry all four.
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 2, "Full Import: Corporate Directory"),
            SystemHealthServer.Derive(JimService.WorkerDelivery, null, DateTime.UtcNow),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 4 * 60));

        var cut = RenderStrip(report);

        foreach (var card in cut.FindAll(".jim-service-health-card"))
        {
            var slots = card.Children.Select(c => c.ClassList.First(cls => cls.StartsWith("jim-service-health-", StringComparison.Ordinal))).ToList();
            Assert.That(slots, Is.EqualTo(new[]
            {
                "jim-service-health-card-top", "jim-service-health-activity", "jim-service-health-condition", "jim-service-health-footer"
            }), $"card {card.GetAttribute("data-service")}");
        }
    }

    [TestCase(ServiceHealthStatus.Healthy, "Healthy", "healthy")]
    [TestCase(ServiceHealthStatus.Degraded, "Degraded", "degraded")]
    [TestCase(ServiceHealthStatus.Unhealthy, "Unhealthy", "unhealthy")]
    public void ServiceHealthStrip_EachStatus_ShowsItsWordOnAPillOfItsColour(ServiceHealthStatus status, string word, string modifier)
    {
        var sync = status switch
        {
            ServiceHealthStatus.Healthy => ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 2),
            ServiceHealthStatus.Degraded => ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 30),
            _ => ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 4 * 60)
        };
        Assume.That(sync.Status, Is.EqualTo(status), "the fixture must produce the status under test through the real derivation");
        var report = ServiceHealthDisplayTests.Report(
            sync,
            ServiceHealthDisplayTests.Derive(JimService.WorkerDelivery, 2),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

        var cut = RenderStrip(report);

        var card = Card(cut, JimService.WorkerSync);
        var pill = card.QuerySelector(".jim-service-health-pill")!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pill.TextContent.Trim(), Is.EqualTo(word));
            Assert.That(pill.ClassList, Does.Contain($"jim-service-health-pill--{modifier}"));
            Assert.That(pill.QuerySelector(".jim-service-health-dot"), Is.Not.Null, "the pill carries the coloured dot");
            Assert.That(card.GetAttribute("data-status"), Is.EqualTo(status.ToString()));
            Assert.That(card.ClassList.Any(c => c.StartsWith("jim-service-health-card--", StringComparison.Ordinal)), Is.False,
                "the pill is the only coloured element; the card itself carries no status modifier");
        }
    }

    [Test]
    public void ServiceHealthStrip_ServiceWithWork_ShowsTheWorkAndHowLongThenTheCondition()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 2, "Full Import: Corporate Directory", 30),
            ServiceHealthDisplayTests.Derive(JimService.WorkerDelivery, 2),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

        var cut = RenderStrip(report);

        var card = Card(cut, JimService.WorkerSync);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(card, ".jim-service-health-activity"), Is.EqualTo("Full Import: Corporate Directory · 12 min"));
            Assert.That(Text(card, ".jim-service-health-condition"), Is.EqualTo("Heartbeat 2 seconds ago"));
            Assert.That(Text(card, ".jim-service-health-footer"), Does.Contain("jim-worker-1").And.Contain("v0.15.0").And.Contain("up 3 d"));
        }
    }

    [Test]
    public void ServiceHealthStrip_IdleService_SaysIdle()
    {
        var cut = RenderStrip(HealthyReport());

        Assert.That(Text(Card(cut, JimService.Scheduler), ".jim-service-health-activity"), Is.EqualTo("Idle"));
    }

    [Test]
    public void ServiceHealthStrip_UnhealthyService_LeadsWithTheReasonAndSaysWhatItWasRunning()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 4 * 60, "Full Import: Corporate Directory"),
            ServiceHealthDisplayTests.Derive(JimService.WorkerDelivery, 2),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

        var cut = RenderStrip(report);

        var card = Card(cut, JimService.WorkerSync);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(card, ".jim-service-health-activity"), Is.EqualTo("No heartbeat for 4 minutes"));
            Assert.That(Text(card, ".jim-service-health-condition"), Is.EqualTo("Was running: Full Import: Corporate Directory"));
            Assert.That(card.TextContent, Does.Not.Contain("· 12 min"), "a dead process is not running anything");
        }
    }

    /// <summary>
    /// An unhealthy service that was idle when it went quiet has nothing it was running, and the slot used to sit
    /// blank on exactly the card an administrator is staring at. The last heartbeat is the next most useful thing
    /// to say there, and every other card in the row keeps its shape.
    /// </summary>
    [Test]
    public void ServiceHealthStrip_UnhealthyIdleService_FillsTheConditionSlotWithItsLastHeartbeat()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 2),
            ServiceHealthDisplayTests.Derive(JimService.WorkerDelivery, 2),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 4 * 60));

        var cut = RenderStrip(report);

        var card = Card(cut, JimService.Scheduler);
        var lastSeen = report.Services.Single(s => s.Service == JimService.Scheduler).LastSeenAt!.Value.ToLocalTime().ToFriendlyDate();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(card, ".jim-service-health-activity"), Is.EqualTo("No heartbeat for 4 minutes"));
            Assert.That(Text(card, ".jim-service-health-condition"), Is.EqualTo($"Last heartbeat {lastSeen}"));
        }
    }

    /// <summary>
    /// The footer's separator dots are drawn by CSS on every item but the first, and the footer must be allowed
    /// to wrap without a wrapped line opening with a dot. The rule that clips a dot at a line start lives in
    /// site.css, where no component test can see it, so this checks the stylesheet carries it rather than
    /// trusting the markup.
    /// </summary>
    [Test]
    public void ServiceHealthStrip_FooterSeparators_AreClippedAtALineStart()
    {
        var css = File.ReadAllText(Path.Join(FindWebProjectRoot(), "wwwroot", "css", "site.css"));

        var footerRule = System.Text.RegularExpressions.Regex.Match(css, @"\.jim-service-health-footer\s*\{[^}]*\}").Value;
        var separatorRule = System.Text.RegularExpressions.Regex.Match(css, @"\.jim-service-health-footer > \* \+ \*::before\s*\{[^}]*\}").Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(footerRule, Does.Contain("overflow: hidden"), "a dot positioned before a line's first item must be clipped");
            Assert.That(separatorRule, Does.Contain("position: absolute"), "the dot sits in the column gap rather than inside the item's box");
        }
    }

    private static string FindWebProjectRoot()
    {
        var directory = new DirectoryInfo(NUnit.Framework.TestContext.CurrentContext.TestDirectory);
        while (directory != null && !Directory.Exists(Path.Join(directory.FullName, "src", "JIM.Web")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "could not locate the repository root from the test directory");
        return Path.Join(directory!.FullName, "src", "JIM.Web");
    }

    [Test]
    public void ServiceHealthStrip_NeverStartedService_RendersHonestlyWithoutCrashing()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 2),
            SystemHealthServer.Derive(JimService.WorkerDelivery, null, DateTime.UtcNow),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

        var cut = RenderStrip(report);

        var card = Card(cut, JimService.WorkerDelivery);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(card, ".jim-service-health-pill"), Is.EqualTo("Unhealthy"));
            Assert.That(Text(card, ".jim-service-health-activity"), Is.EqualTo("Never started"));
            Assert.That(Text(card, ".jim-service-health-condition"), Is.Empty);
            Assert.That(Text(card, ".jim-service-health-footer"), Is.Empty, "nothing to identify an instance that never existed");
        }
    }

    [Test]
    public void ServiceHealthStrip_ServiceOnAnotherVersion_GetsTheChipAndTooltipOnItsCardOnly()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 2, version: "0.14.0"),
            ServiceHealthDisplayTests.Derive(JimService.WorkerDelivery, 2),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

        var cut = RenderStrip(report);

        var skewTooltips = cut.FindComponents<MudTooltip>()
            .Where(t => t.Instance.Text == ServiceHealthStrip.VersionSkewTooltip)
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(skewTooltips, Has.Count.EqualTo(1), "only the service on the other version is flagged");
            Assert.That(Card(cut, JimService.WorkerSync).QuerySelector(".jim-service-health-skew")!.TextContent.Trim(), Is.EqualTo("differs from portal"));
            Assert.That(Card(cut, JimService.WorkerDelivery).QuerySelector(".jim-service-health-skew"), Is.Null);
            Assert.That(Card(cut, JimService.Scheduler).QuerySelector(".jim-service-health-skew"), Is.Null);
        }
    }

    [Test]
    public void ServiceHealthStrip_LiveUpdatesIndicator_FollowsTheRelay()
    {
        var cut = RenderStrip(HealthyReport());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(LiveIndicator(cut).TextContent.Trim(), Is.EqualTo("Live updates connected"));
            Assert.That(LiveIndicator(cut).QuerySelector(".jim-service-health-dot")!.ClassList, Does.Contain("jim-service-health-dot--healthy"));
        }

        _notifications.IsRealTimeAvailable = false;
        _notifications.RaiseRealTimeAvailabilityChanged(false);

        cut.WaitForAssertion(() =>
        {
            Assert.That(LiveIndicator(cut).TextContent.Trim(), Is.EqualTo("Live updates reconnecting"));
            Assert.That(LiveIndicator(cut).QuerySelector(".jim-service-health-dot")!.ClassList, Does.Contain("jim-service-health-dot--degraded"));
        });
    }

    /// <summary>
    /// A relay the test drives by hand. Availability is settable so the indicator can be shown both ways.
    /// </summary>
    private sealed class FakeUiNotificationService : IUiNotificationService
    {
        public event Action<WorkerTaskChangeNotification>? WorkerTaskChanged;

        public event Action<Guid>? ActivityProgressChanged;

        public event Action<bool>? RealTimeAvailabilityChanged;

        public bool IsRealTimeAvailable { get; set; } = true;

        public void RaiseRealTimeAvailabilityChanged(bool available) => RealTimeAvailabilityChanged?.Invoke(available);

        // Declared by the interface; referenced here so the compiler does not warn about events never raised.
        public void RaiseOthers()
        {
            WorkerTaskChanged?.Invoke(null!);
            ActivityProgressChanged?.Invoke(Guid.Empty);
        }
    }
}
