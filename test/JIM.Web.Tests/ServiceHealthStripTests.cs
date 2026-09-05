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
/// The strip at the top of Operations: one card per background service from the health report the page hands it,
/// plus a card for the notification relay the page itself depends on. What these pin is what an administrator
/// reads off a card at a glance: the state word and its colour, the headline (the work in hand, or when the
/// service was last heard from), and the amber version that says the Worker was not upgraded with the portal.
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
        ServiceHealthDisplayTests.Derive(JimService.WorkerPasswordDelivery, 2),
        ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

    private IRenderedComponent<ServiceHealthStrip> RenderStrip(ServiceHealthReport report) =>
        Render<ServiceHealthStrip>(p => p.Add(c => c.Report, report));

    private static IElement Card(IRenderedComponent<ServiceHealthStrip> cut, JimService service) =>
        cut.Find($".jim-service-health-card[data-service='{service}']");

    private static IElement RelayCard(IRenderedComponent<ServiceHealthStrip> cut) =>
        cut.Find(".jim-service-health-card[data-service='LiveUpdates']");

    [Test]
    public void ServiceHealthStrip_Report_RendersACardPerServiceAndOneForLiveUpdates()
    {
        var cut = RenderStrip(HealthyReport());

        var cards = cut.FindAll(".jim-service-health-card");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cards, Has.Count.EqualTo(4));
            Assert.That(cards.Select(c => c.QuerySelector(".jim-service-health-label")!.TextContent.Trim()),
                Is.EqualTo(new[] { "Worker · Sync", "Worker · Passwords", "Scheduler", "Live updates" }));
        }
    }

    [TestCase(ServiceHealthState.Running, "Running", "running", Color.Success)]
    [TestCase(ServiceHealthState.Stale, "Stale", "stale", Color.Warning)]
    [TestCase(ServiceHealthState.NoProgress, "No progress", "no-progress", Color.Warning)]
    [TestCase(ServiceHealthState.NotSeen, "Not seen", "not-seen", Color.Error)]
    public void ServiceHealthStrip_EachState_ShowsItsWordColourAndBorder(ServiceHealthState state, string word, string modifier, Color colour)
    {
        var sync = state switch
        {
            ServiceHealthState.Running => ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 2),
            ServiceHealthState.Stale => ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 30),
            ServiceHealthState.NoProgress => ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 2, "Full Import: Corporate Directory", 12 * 60),
            _ => ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 4 * 60)
        };
        Assume.That(sync.State, Is.EqualTo(state), "the fixture must produce the state under test through the real derivation");
        var report = ServiceHealthDisplayTests.Report(
            sync,
            ServiceHealthDisplayTests.Derive(JimService.WorkerPasswordDelivery, 2),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

        var cut = RenderStrip(report);

        var card = Card(cut, JimService.WorkerSync);
        // The colour travels on the MudText's Color parameter (JIM's choice, not MudBlazor's class names). The
        // WorkerSync card is the only one in the state under test, so the state word finds the right MudText.
        var stateWord = cut.FindComponents<MudText>()
            .First(t => t.Instance.Class?.Contains("jim-service-health-state") == true && t.Markup.Contains(word));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(card.ClassList, Does.Contain($"jim-service-health-card--{modifier}"));
            Assert.That(card.QuerySelector(".jim-service-health-state")!.TextContent.Trim(), Is.EqualTo(word));
            Assert.That(stateWord.Instance.Color, Is.EqualTo(colour));
        }
    }

    [Test]
    public void ServiceHealthStrip_ServiceWithWork_HeadlinesTheWorkAndSaysHowLongItHasRun()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 2, "Full Import: Corporate Directory", 30),
            ServiceHealthDisplayTests.Derive(JimService.WorkerPasswordDelivery, 2),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

        var cut = RenderStrip(report);

        var card = Card(cut, JimService.WorkerSync);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(card.QuerySelector(".jim-service-health-headline")!.TextContent.Trim(), Is.EqualTo("Full Import: Corporate Directory"));
            Assert.That(card.TextContent, Does.Contain("Running for 12 min"));
            Assert.That(card.TextContent, Does.Contain("jim-worker-1"));
            Assert.That(card.TextContent, Does.Contain("up 3 d"));
        }
    }

    [Test]
    public void ServiceHealthStrip_IdleService_HeadlinesIdle()
    {
        var cut = RenderStrip(HealthyReport());

        Assert.That(Card(cut, JimService.Scheduler).QuerySelector(".jim-service-health-headline")!.TextContent.Trim(), Is.EqualTo("Idle"));
    }

    [Test]
    public void ServiceHealthStrip_NotSeenService_HeadlinesTheLastHeartbeat()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 4 * 60, "Full Import: Corporate Directory"),
            ServiceHealthDisplayTests.Derive(JimService.WorkerPasswordDelivery, 2),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

        var cut = RenderStrip(report);

        var card = Card(cut, JimService.WorkerSync);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(card.QuerySelector(".jim-service-health-headline")!.TextContent.Trim(), Is.EqualTo("Last heartbeat 4 min ago"));
            Assert.That(card.TextContent, Does.Not.Contain("Running for"), "a dead process is not running anything");
        }
    }

    [Test]
    public void ServiceHealthStrip_PasswordDeliveryNeverReported_RendersHonestlyWithoutCrashing()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 2),
            SystemHealthServer.Derive(JimService.WorkerPasswordDelivery, null, DateTime.UtcNow),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

        var cut = RenderStrip(report);

        var card = Card(cut, JimService.WorkerPasswordDelivery);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(card.ClassList, Does.Contain("jim-service-health-card--not-seen"));
            Assert.That(card.QuerySelector(".jim-service-health-state")!.TextContent.Trim(), Is.EqualTo("Not seen"));
            Assert.That(card.QuerySelector(".jim-service-health-headline")!.TextContent.Trim(), Is.EqualTo("Never reported"));
        }
    }

    [Test]
    public void ServiceHealthStrip_ServiceOnAnotherVersion_ShowsTheVersionInAmberWithTheUpgradeTooltip()
    {
        var report = ServiceHealthDisplayTests.Report(
            ServiceHealthDisplayTests.Derive(JimService.WorkerSync, 2, version: "0.14.0"),
            ServiceHealthDisplayTests.Derive(JimService.WorkerPasswordDelivery, 2),
            ServiceHealthDisplayTests.Derive(JimService.Scheduler, 2));

        var cut = RenderStrip(report);

        var skewTooltips = cut.FindComponents<MudTooltip>()
            .Where(t => t.Instance.Text == ServiceHealthStrip.VersionSkewTooltip)
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(skewTooltips, Has.Count.EqualTo(1), "only the service on the other version is flagged");
            Assert.That(Card(cut, JimService.WorkerSync).QuerySelector(".jim-service-health-version--skew"), Is.Not.Null);
            Assert.That(Card(cut, JimService.Scheduler).QuerySelector(".jim-service-health-version--skew"), Is.Null);
        }
    }

    [Test]
    public void ServiceHealthStrip_LiveUpdatesCard_FollowsTheRelay()
    {
        var cut = RenderStrip(HealthyReport());

        Assert.That(RelayCard(cut).QuerySelector(".jim-service-health-state")!.TextContent.Trim(), Is.EqualTo("Connected"));
        Assert.That(RelayCard(cut).ClassList, Does.Contain("jim-service-health-card--connected"));

        _notifications.IsRealTimeAvailable = false;
        _notifications.RaiseRealTimeAvailabilityChanged(false);

        cut.WaitForAssertion(() =>
        {
            Assert.That(RelayCard(cut).QuerySelector(".jim-service-health-state")!.TextContent.Trim(), Is.EqualTo("Reconnecting"));
            Assert.That(RelayCard(cut).ClassList, Does.Contain("jim-service-health-card--reconnecting"));
        });
    }

    /// <summary>
    /// A relay the test drives by hand. Availability is settable so the Live updates card can be shown both ways.
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
