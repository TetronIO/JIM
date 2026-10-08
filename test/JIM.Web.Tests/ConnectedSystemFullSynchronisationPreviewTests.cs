// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Claims;
using System.Text.Json;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Web.Models;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Full Synchronisation preview on a Connected System's page (#1530): where it is started from the drift notice or
/// a Run Profile, found again on return, and acted on. The preview runs on long after the page is left, so the panel
/// reattaches to the latest one, until a Full Synchronisation has run since it, when its job is done. Running from the
/// panel records the preview on the run only while nothing has overtaken it, as a deletion does (#134). The shared
/// preview panel is stubbed, rendering only the actions this host hands it; its own behaviour has its own tests.
/// </summary>
[TestFixture]
public class ConnectedSystemFullSynchronisationPreviewTests : JimComponentTestContext
{
    private const int ConnectedSystemId = 7;
    private const int RunProfileId = 70;
    private const string PanelStubMarker = "jim-full-sync-preview-panel-stub";
    private const string RunMarker = "jim-full-sync-run";
    private const string StaleMarker = "jim-full-sync-preview-stale";

    private Mock<IConfigurationChangePreviewRepository> _previews = null!;
    private Mock<IActivityRepository> _activities = null!;
    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private Mock<ITaskingRepository> _tasking = null!;
    private Mock<IConfigurationChangePreviewStarter> _starter = null!;
    private JimApplication _jim = null!;
    private ConfigurationChangePreview? _latestPreview;
    private Activity? _lastCompletedRun;
    private ConfigurationChangePreviewStaleness _staleness = new(null, null);
    private SynchronisationWorkerTask? _queuedRun;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _previews = new Mock<IConfigurationChangePreviewRepository>();
        _activities = new Mock<IActivityRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        _tasking = new Mock<ITaskingRepository>();
        var metaverse = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.ConfigurationChangePreviews).Returns(_previews.Object);
        repository.Setup(r => r.Activity).Returns(_activities.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        repository.Setup(r => r.Tasking).Returns(_tasking.Object);
        repository.Setup(r => r.Metaverse).Returns(metaverse.Object);

        _previews.Setup(r => r.GetLatestConnectedSystemPreviewAsync(ConfigurationChangePreviewSurface.ConnectedSystemFullSynchronisation, ConnectedSystemId))
            .ReturnsAsync(() => _latestPreview);
        _previews.Setup(r => r.GetPreviewAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _latestPreview?.ActivityId == id ? _latestPreview : null);
        _activities.Setup(r => r.GetActivityAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _latestPreview?.ActivityId == id ? _latestPreview.Activity : null);
        _activities.Setup(r => r.GetPreviewStalenessSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(() => _staleness);
        _activities.Setup(r => r.GetLatestCompletedFullSynchronisationAsync(ConnectedSystemId)).ReturnsAsync(() => _lastCompletedRun);
        _activities.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var connectedSystem = System();
        _connectedSystems.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, false)).ReturnsAsync(connectedSystem);
        _connectedSystems.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _connectedSystems.Setup(r => r.GetConnectedSystemRunProfilesAsync(ConnectedSystemId)).ReturnsAsync(
        [
            new ConnectedSystemRunProfile { Id = 71, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport, ConnectedSystemId = ConnectedSystemId },
            new ConnectedSystemRunProfile { Id = RunProfileId, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation, ConnectedSystemId = ConnectedSystemId }
        ]);
        _tasking.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(task => _queuedRun = task as SynchronisationWorkerTask)
            .Returns(Task.CompletedTask);

        var administratorId = Guid.NewGuid();
        AddAuthorization().SetAuthorized("Ada Lovelace").SetClaims(new Claim(Constants.BuiltInClaims.MetaverseObjectId, administratorId.ToString()));
        metaverse.Setup(r => r.GetMetaverseObjectAsync(administratorId)).ReturnsAsync(new MetaverseObject { Id = administratorId });

        _starter = new Mock<IConfigurationChangePreviewStarter>();
        Services.AddSingleton(_starter.Object);
        _jim = new JimApplication(repository.Object);
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_jim));
    }

    [SetUp]
    public void SetUp()
    {
        ComponentFactories.AddStub<ConfigurationChangePreviewPanel>(panel => builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "data-testid", PanelStubMarker);
            builder.AddAttribute(2, "data-activity-id", panel.Get(p => p.ActivityId).ToString());
            builder.AddContent(3, panel.Get(p => p.HeaderActions));
            builder.CloseElement();
        });
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        _latestPreview = null;
        _lastCompletedRun = null;
        _staleness = new ConfigurationChangePreviewStaleness(null, null);
        _queuedRun = null;
    }

    [Test]
    public void FullSyncPreview_NeverPreviewed_ShowsNoPanel()
    {
        var cut = RenderHost();

        Assert.That(cut.FindAll($"[data-testid='{PanelStubMarker}']"), Is.Empty);
    }

    [Test]
    public void FullSyncPreview_LatestPreviewWithNoRunSince_ReattachesToIt()
    {
        _latestPreview = Preview(ActivityStatus.InProgress, ConfigurationChangePreviewStageStatus.InProgress);

        var cut = RenderHost();

        Assert.That(PanelActivityId(cut), Is.EqualTo(_latestPreview.ActivityId.ToString()));
    }

    [Test]
    public void FullSyncPreview_LatestPreviewFollowedByAFullSynchronisation_IsNotReattached()
    {
        // The run it previewed has happened, and recorded the preview if it was started from it; reattaching would show
        // what a run that is over would do.
        _latestPreview = Preview(ActivityStatus.Complete, ConfigurationChangePreviewStageStatus.Complete);
        _lastCompletedRun = new Activity { Executed = _latestPreview.Activity.Created.AddMinutes(2) };

        var cut = RenderHost();

        Assert.That(cut.FindAll($"[data-testid='{PanelStubMarker}']"), Is.Empty);
    }

    [Test]
    public void FullSyncPreview_StartRequest_StartsAFullSynchronisationPreviewOfThisSystemOnce()
    {
        var started = Guid.CreateVersion7();
        ConfigurationChangePreviewRequest? request = null;
        _starter.Setup(s => s.StartAsync(It.IsAny<ConfigurationChangePreviewRequest>()))
            .Callback<ConfigurationChangePreviewRequest>(r => request = r)
            .ReturnsAsync(started);
        var startRequest = new FullSynchronisationPreviewRequest(Guid.NewGuid(), RunProfileId);
        Guid? handled = null;

        var cut = Render<ConnectedSystemFullSynchronisationPreview>(p => p
            .Add(x => x.ConnectedSystem, System())
            .Add(x => x.StartRequest, startRequest)
            .Add(x => x.StartRequestHandled, id => handled = id));
        cut.WaitForState(() => cut.FindAll($"[data-testid='{PanelStubMarker}']").Count == 1, TimeSpan.FromSeconds(2));
        cut.Render();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request?.Surface, Is.EqualTo(ConfigurationChangePreviewSurface.ConnectedSystemFullSynchronisation));
            Assert.That(request?.TargetId, Is.EqualTo(ConnectedSystemId));
            Assert.That(PanelActivityId(cut), Is.EqualTo(started.ToString()));
            Assert.That(handled, Is.EqualTo(startRequest.Id), "the page clears a request once it is handled, so a revisit cannot start another");
        }
        _starter.Verify(s => s.StartAsync(It.IsAny<ConfigurationChangePreviewRequest>()), Times.Once,
            "a request is handled once however often the host renders");
    }

    [Test]
    public void FullSyncPreview_RunFullSynchronisationFromACurrentPreview_QueuesTheRunCitingIt()
    {
        _latestPreview = Preview(ActivityStatus.Complete, ConfigurationChangePreviewStageStatus.Complete);

        var cut = RenderHost();
        cut.Find($"[data-testid='{RunMarker}']").Click();
        cut.WaitForState(() => _queuedRun != null, TimeSpan.FromSeconds(2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_queuedRun!.ConnectedSystemRunProfileId, Is.EqualTo(RunProfileId));
            Assert.That(_queuedRun!.PreviewActivityId, Is.EqualTo(_latestPreview.ActivityId),
                "the run's Activity records the preview its administrator read");
        }
    }

    [Test]
    public void FullSyncPreview_RunFullSynchronisationFromAStalePreview_AsksFirstNamingWhatOvertookIt()
    {
        // Staleness is judged across the whole deployment, so a preview is usually overtaken before anyone acts on it
        // (#2022). The administrator is asked before the run queues, and told what overtook the preview.
        var (provider, cut) = RenderStaleHost();

        cut.Find($"[data-testid='{RunMarker}']").Click();
        provider.WaitForState(() => provider.FindAll("button").Any(b => b.TextContent.Contains("Run anyway")), TimeSpan.FromSeconds(2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Markup, Does.Contain("Delta Import"), "the question names what overtook the preview");
            Assert.That(_queuedRun, Is.Null, "nothing queues until the administrator answers");
        }
    }

    [Test]
    public void FullSyncPreview_RunAnywayFromAStalePreview_QueuesTheRunCitingIt()
    {
        // The run cites the preview its administrator read; the server records that the preview was out of date, rather
        // than the link being dropped and the run reading as though no preview was read at all.
        var (provider, cut) = RenderStaleHost();

        cut.Find($"[data-testid='{RunMarker}']").Click();
        ClickDialogButton(provider, "Run anyway");
        cut.WaitForState(() => _queuedRun != null, TimeSpan.FromSeconds(2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_queuedRun!.PreviewActivityId, Is.EqualTo(_latestPreview!.ActivityId));
            Assert.That(Services.GetRequiredService<MudBlazor.ISnackbar>().ShownSnackbars.Where(s => s.Severity == MudBlazor.Severity.Warning),
                Is.Empty, "the administrator has just been asked about the out-of-date preview; warning again says it twice");
        }
    }

    [Test]
    public void FullSyncPreview_PreviewAgainFromAStalePreview_StartsAPreviewAndQueuesNoRun()
    {
        _starter.Setup(s => s.StartAsync(It.IsAny<ConfigurationChangePreviewRequest>())).ReturnsAsync(Guid.CreateVersion7());
        var (provider, cut) = RenderStaleHost();

        cut.Find($"[data-testid='{RunMarker}']").Click();
        ClickDialogButton(provider, "Preview again");
        cut.WaitForState(() => _starter.Invocations.Count > 0, TimeSpan.FromSeconds(2));

        using (Assert.EnterMultipleScope())
        {
            _starter.Verify(s => s.StartAsync(It.Is<ConfigurationChangePreviewRequest>(r =>
                r.Surface == ConfigurationChangePreviewSurface.ConnectedSystemFullSynchronisation)), Times.Once);
            Assert.That(_queuedRun, Is.Null);
        }
    }

    [Test]
    public void FullSyncPreview_RunFullSynchronisationFromACurrentPreview_AsksNothing()
    {
        _latestPreview = Preview(ActivityStatus.Complete, ConfigurationChangePreviewStageStatus.Complete);
        var provider = Render<MudBlazor.MudDialogProvider>();

        var cut = RenderHost();
        cut.Find($"[data-testid='{RunMarker}']").Click();
        cut.WaitForState(() => _queuedRun != null, TimeSpan.FromSeconds(2));

        Assert.That(provider.FindAll("button").Where(b => b.TextContent.Contains("Run anyway")), Is.Empty);
    }

    private (IRenderedComponent<MudBlazor.MudDialogProvider> Provider, IRenderedComponent<ConnectedSystemFullSynchronisationPreview> Host) RenderStaleHost()
    {
        _latestPreview = Preview(ActivityStatus.Complete, ConfigurationChangePreviewStageStatus.Complete);
        _staleness = new ConfigurationChangePreviewStaleness(new PreviewOvertakingActivity(Guid.NewGuid(), DateTime.UtcNow,
            ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute, "Delta Import", "HR Import"), null);
        var provider = Render<MudBlazor.MudDialogProvider>();
        var cut = RenderHost();
        Assert.That(cut.FindAll($"[data-testid='{StaleMarker}']"), Has.Count.EqualTo(1));
        return (provider, cut);
    }

    private static void ClickDialogButton(IRenderedComponent<MudBlazor.MudDialogProvider> provider, string text)
    {
        provider.WaitForState(() => provider.FindAll("button").Any(b => b.TextContent.Contains(text)), TimeSpan.FromSeconds(2));
        provider.FindAll("button").First(b => b.TextContent.Contains(text)).Click();
    }

    [Test]
    public void FullSyncPreview_RunningPreview_OffersNoRun()
    {
        _latestPreview = Preview(ActivityStatus.InProgress, ConfigurationChangePreviewStageStatus.InProgress);

        var cut = RenderHost();

        Assert.That(cut.FindAll($"[data-testid='{RunMarker}']"), Is.Empty, "a preview that has not answered informs nothing");
    }

    private IRenderedComponent<ConnectedSystemFullSynchronisationPreview> RenderHost()
    {
        var cut = Render<ConnectedSystemFullSynchronisationPreview>(p => p.Add(x => x.ConnectedSystem, System()));
        cut.WaitForState(() => !cut.Markup.Contains("jim-full-sync-preview-loading"), TimeSpan.FromSeconds(2));
        return cut;
    }

    private static string? PanelActivityId(IRenderedComponent<ConnectedSystemFullSynchronisationPreview> cut) =>
        cut.FindAll($"[data-testid='{PanelStubMarker}']").SingleOrDefault()?.GetAttribute("data-activity-id");

    private static ConnectedSystem System() => new()
    {
        Id = ConnectedSystemId,
        Name = "Yellowstone HR",
        Status = ConnectedSystemStatus.Active,
        SettingValues = [],
        ConnectorDefinition = new ConnectorDefinition { Name = "CSV" }
    };

    private static ConfigurationChangePreview Preview(ActivityStatus activityStatus, ConfigurationChangePreviewStageStatus stageStatus)
    {
        var activityId = Guid.CreateVersion7();
        return new ConfigurationChangePreview
        {
            ActivityId = activityId,
            Activity = new Activity
            {
                Id = activityId,
                Created = DateTime.UtcNow.AddMinutes(-4),
                Status = activityStatus,
                TargetType = ActivityTargetType.ConnectedSystem,
                TargetOperationType = ActivityTargetOperationType.Preview,
                ConnectedSystemId = ConnectedSystemId
            },
            Surface = ConfigurationChangePreviewSurface.ConnectedSystemFullSynchronisation,
            ValidationStatus = ConfigurationChangePreviewStageStatus.Complete,
            ImpactCountsStatus = stageStatus,
            SummaryStatus = stageStatus,
            DeltasStatus = stageStatus,
            ImpactCounts = JsonSerializer.Serialize(new List<PreviewImpactCount>())
        };
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }
}
