// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Operations;
using JIM.Web.Services;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The banner MainLayout shows administrators on every page when a service is down or a task has stalled. It reads
/// the same heartbeats the strip reads, through a real <see cref="JimApplication"/> over a mocked repository, so
/// these exercise the derivation as well as the rendering. What they pin: that it says nothing at all when nothing
/// is wrong (no wrapper element to push the page down), that the severity and the sentence match the fault, that it
/// stays out of the way on Operations where the strip already tells the story, and that a Worker outage is named
/// once however many of its services it takes down.
/// </summary>
[TestFixture]
public class ServiceHealthBannerTests : JimComponentTestContext
{
    private Mock<ISystemRepository> _systemRepository = null!;
    private List<ServiceHeartbeat> _heartbeats = [];
    private int _reads;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _systemRepository = new Mock<ISystemRepository>();
        repository.Setup(r => r.System).Returns(_systemRepository.Object);
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(new JimApplication(repository.Object)));
    }

    [SetUp]
    public void SetUp()
    {
        _systemRepository.Reset();
        _heartbeats = [];
        _reads = 0;
        _systemRepository
            .Setup(r => r.GetLatestServiceHeartbeatsAsync())
            .Callback(() => Interlocked.Increment(ref _reads))
            .ReturnsAsync(() => [.. _heartbeats]);
    }

    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    private void Arrange(params ServiceHeartbeat[] heartbeats) => _heartbeats = [.. heartbeats];

    private static ServiceHeartbeat Heartbeat(
        JimService service,
        int ageSeconds,
        string? currentWork = null,
        int? progressAgeSeconds = null)
    {
        var now = DateTime.UtcNow;
        return new ServiceHeartbeat
        {
            Service = service,
            InstanceId = "jim-worker-1:4f2a",
            HostName = "jim-worker-1",
            Version = "0.15.0",
            StartedAt = now.AddDays(-3),
            LastSeenAt = now.AddSeconds(-ageSeconds),
            CurrentWork = currentWork,
            CurrentWorkStartedAt = currentWork == null ? null : now.AddMinutes(-12),
            LastProgressAt = progressAgeSeconds.HasValue ? now.AddSeconds(-progressAgeSeconds.Value) : null
        };
    }

    /// <summary>
    /// Renders the banner and waits for its first read of the heartbeats to have happened and been rendered. The
    /// read runs in OnInitializedAsync, so once the count moves the render that follows it is what a hidden-case
    /// assertion needs to have seen; a short settle covers the gap between the two.
    /// </summary>
    private async Task<IRenderedComponent<ServiceHealthBanner>> RenderAndReadAsync()
    {
        var cut = Render<ServiceHealthBanner>();
        for (var attempt = 0; attempt < 200 && Volatile.Read(ref _reads) == 0; attempt++)
            await Task.Delay(50);
        Assert.That(_reads, Is.GreaterThan(0), "the banner reads the heartbeats when it initialises");
        await Task.Delay(100);
        return cut;
    }

    [Test]
    public async Task ServiceHealthBanner_EveryServiceRunning_RendersNothingAtAll()
    {
        Arrange(Heartbeat(JimService.WorkerSync, 2), Heartbeat(JimService.WorkerPasswordDelivery, 2), Heartbeat(JimService.Scheduler, 2));

        var cut = await RenderAndReadAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.HasComponent<MudAlert>(), Is.False);
            Assert.That(cut.Markup.Trim(), Is.Empty, "a healthy banner leaves no wrapper element to push the page down");
        }
    }

    [Test]
    public async Task ServiceHealthBanner_OverdueHeartbeat_RendersNothing()
    {
        Arrange(Heartbeat(JimService.WorkerSync, 30), Heartbeat(JimService.WorkerPasswordDelivery, 2), Heartbeat(JimService.Scheduler, 2));

        var cut = await RenderAndReadAsync();

        Assert.That(cut.HasComponent<MudAlert>(), Is.False);
    }

    [Test]
    public async Task ServiceHealthBanner_PasswordDeliveryNeverReportedBesideARunningWorker_RendersNothing()
    {
        // The shape every installation has until its Worker carries the password delivery service: no banner,
        // because the Worker is alive; the strip's card is where the gap is shown.
        Arrange(Heartbeat(JimService.WorkerSync, 2), Heartbeat(JimService.Scheduler, 2));

        var cut = await RenderAndReadAsync();

        Assert.That(cut.HasComponent<MudAlert>(), Is.False);
    }

    [Test]
    public async Task ServiceHealthBanner_WorkerWithNoHeartbeat_ShowsAnErrorNamingTheWorkerOnce()
    {
        // Sync gone quiet and password delivery never reported: one Worker, one sentence.
        Arrange(Heartbeat(JimService.WorkerSync, 4 * 60), Heartbeat(JimService.Scheduler, 2));

        var cut = await RenderAndReadAsync();

        cut.WaitForAssertion(() =>
        {
            var alert = cut.FindComponent<MudAlert>();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(alert.Instance.Severity, Is.EqualTo(Severity.Error));
                Assert.That(alert.Instance.Variant, Is.EqualTo(Variant.Outlined));
                Assert.That(cut.Markup, Does.Contain("The Worker has not reported for 4 minutes. Nothing is being synchronised or delivered; queued work is safe and resumes when it returns."));
                Assert.That(CountOf(cut.Markup, "Worker"), Is.EqualTo(1));
            }
        });
    }

    [Test]
    public async Task ServiceHealthBanner_BothWorkerServicesWithNoHeartbeat_NamesTheWorkerOnce()
    {
        Arrange(Heartbeat(JimService.WorkerSync, 4 * 60), Heartbeat(JimService.WorkerPasswordDelivery, 4 * 60), Heartbeat(JimService.Scheduler, 2));

        var cut = await RenderAndReadAsync();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("The Worker has not reported for 4 minutes."));
            Assert.That(CountOf(cut.Markup, "Worker"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ServiceHealthBanner_WorkerStalled_ShowsAWarningNamingTheWork()
    {
        Arrange(
            Heartbeat(JimService.WorkerSync, 2, "Full Import: Corporate Directory", 12 * 60),
            Heartbeat(JimService.WorkerPasswordDelivery, 2),
            Heartbeat(JimService.Scheduler, 2));

        var cut = await RenderAndReadAsync();

        cut.WaitForAssertion(() =>
        {
            var alert = cut.FindComponent<MudAlert>();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(alert.Instance.Severity, Is.EqualTo(Severity.Warning));
                Assert.That(cut.Markup, Does.Contain("The Worker has made no progress on Full Import: Corporate Directory for 12 minutes."));
            }
        });
    }

    [Test]
    public async Task ServiceHealthBanner_Shown_LinksToOperationsAndLogs()
    {
        Arrange(Heartbeat(JimService.WorkerSync, 4 * 60), Heartbeat(JimService.Scheduler, 2));

        var cut = await RenderAndReadAsync();

        cut.WaitForAssertion(() =>
        {
            var links = cut.FindComponents<MudLink>().Select(l => (l.Instance.Href, Text: l.Markup)).ToList();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(links.Any(l => l.Href == "/admin/operations" && l.Text.Contains("Operations")), Is.True);
                Assert.That(links.Any(l => l.Href == "/admin/logs" && l.Text.Contains("Logs")), Is.True);
            }
        });
    }

    [Test]
    public async Task ServiceHealthBanner_OnTheOperationsPage_RendersNothingEvenWhenUnhealthy()
    {
        Arrange(Heartbeat(JimService.WorkerSync, 4 * 60), Heartbeat(JimService.Scheduler, 2));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/operations?t=queue");

        var cut = await RenderAndReadAsync();

        Assert.That(cut.HasComponent<MudAlert>(), Is.False, "the strip on Operations already says it; a banner above it would say it twice");
    }

    [Test]
    public async Task ServiceHealthBanner_LeavingTheOperationsPage_ShowsTheBannerAgain()
    {
        Arrange(Heartbeat(JimService.WorkerSync, 4 * 60), Heartbeat(JimService.Scheduler, 2));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/admin/operations");
        var cut = await RenderAndReadAsync();
        Assume.That(cut.HasComponent<MudAlert>(), Is.False);

        navigation.NavigateTo("/admin/connected-systems");

        cut.WaitForAssertion(() => Assert.That(cut.HasComponent<MudAlert>(), Is.True));
    }

    private static int CountOf(string text, string word)
    {
        var count = 0;
        for (var index = text.IndexOf(word, StringComparison.Ordinal); index >= 0; index = text.IndexOf(word, index + word.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }
}
