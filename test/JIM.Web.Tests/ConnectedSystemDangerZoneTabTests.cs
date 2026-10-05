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
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Danger Zone's deletion impact preview (#134): where an administrator previews what deleting the system would do,
/// and where they find that preview again. The preview runs on long after the page is left, so reattaching to it on
/// return is the behaviour that matters most; starting a second one beside it would cost a full run for nothing.
/// The shared preview panel is stubbed: its own behaviour is covered by its own tests, and what this fixture asserts is
/// which preview the tab hands it.
/// </summary>
[TestFixture]
public class ConnectedSystemDangerZoneTabTests : JimComponentTestContext
{
    private const int ConnectedSystemId = 7;
    private const string PreviewButtonMarker = "jim-preview-deletion-impact";
    private const string PanelStubMarker = "jim-deletion-preview-panel-stub";
    private const string StaleMarker = "jim-deletion-impact-stale";

    private Mock<IConfigurationChangePreviewRepository> _mockPreviewRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IConfigurationChangePreviewStarter> _mockStarter = null!;
    private JimApplication _jim = null!;
    private ConfigurationChangePreview? _latestPreview;
    private ConfigurationChangePreviewStaleness _staleness = new(null, null);

    protected override void ConfigureAdditionalServices()
    {
        var mockRepository = new Mock<IRepository>();
        _mockPreviewRepo = new Mock<IConfigurationChangePreviewRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        var mockMetaverseRepo = new Mock<IMetaverseRepository>();
        mockRepository.Setup(r => r.ConfigurationChangePreviews).Returns(_mockPreviewRepo.Object);
        mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        mockRepository.Setup(r => r.Metaverse).Returns(mockMetaverseRepo.Object);

        _mockPreviewRepo
            .Setup(r => r.GetLatestConnectedSystemPreviewAsync(ConfigurationChangePreviewSurface.ConnectedSystemDeletion, ConnectedSystemId))
            .ReturnsAsync(() => _latestPreview);
        _mockActivityRepo
            .Setup(r => r.GetActivityAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _latestPreview?.ActivityId == id ? _latestPreview.Activity : null);
        _mockActivityRepo
            .Setup(r => r.GetPreviewStalenessSinceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(() => _staleness);

        var administratorId = Guid.NewGuid();
        AddAuthorization().SetAuthorized("Ada Lovelace").SetClaims(new Claim(Constants.BuiltInClaims.MetaverseObjectId, administratorId.ToString()));
        mockMetaverseRepo.Setup(r => r.GetMetaverseObjectAsync(administratorId)).ReturnsAsync(new MetaverseObject { Id = administratorId });

        _mockStarter = new Mock<IConfigurationChangePreviewStarter>();
        Services.AddSingleton(_mockStarter.Object);

        _jim = new JimApplication(mockRepository.Object);
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_jim));
    }

    [SetUp]
    public void SetUp()
    {
        ComponentFactories.AddStub<ConfigurationChangePreviewPanel>(panel =>
            $"<div data-testid=\"{PanelStubMarker}\" data-activity-id=\"{panel.Get(p => p.ActivityId)}\"></div>");
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        _latestPreview = null;
        _staleness = new ConfigurationChangePreviewStaleness(null, null);
    }

    [Test]
    public void DangerZone_NeverPreviewed_OffersAPreviewAndShowsNoPanel()
    {
        var cut = RenderTab();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find($"[data-testid='{PreviewButtonMarker}']").TextContent, Does.Contain("Preview deletion impact"));
            Assert.That(cut.FindAll($"[data-testid='{PanelStubMarker}']"), Is.Empty);
        }
    }

    [Test]
    public void DangerZone_APreviewExists_ReattachesToIt()
    {
        _latestPreview = Preview(ActivityStatus.InProgress, ConfigurationChangePreviewStageStatus.InProgress);

        var cut = RenderTab();

        Assert.That(PanelActivityId(cut), Is.EqualTo(_latestPreview.ActivityId.ToString()));
    }

    [Test]
    public void DangerZone_PreviewDeletionImpact_StartsADeletionPreviewOfThisSystemAndShowsIt()
    {
        var started = Guid.CreateVersion7();
        ConfigurationChangePreviewRequest? request = null;
        _mockStarter.Setup(s => s.StartAsync(It.IsAny<ConfigurationChangePreviewRequest>()))
            .Callback<ConfigurationChangePreviewRequest>(r => request = r)
            .ReturnsAsync(started);
        var cut = RenderTab();

        cut.Find($"[data-testid='{PreviewButtonMarker}']").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request?.Surface, Is.EqualTo(ConfigurationChangePreviewSurface.ConnectedSystemDeletion));
            Assert.That(request?.TargetId, Is.EqualTo(ConnectedSystemId));
            Assert.That(request?.ProposedConfiguration, Is.InstanceOf<ConnectedSystemDeletionProposal>());
            Assert.That(PanelActivityId(cut), Is.EqualTo(started.ToString()));
        }
    }

    [Test]
    public void DangerZone_SizeQuestionDeclined_ShowsNoPanel()
    {
        _mockStarter.Setup(s => s.StartAsync(It.IsAny<ConfigurationChangePreviewRequest>())).ReturnsAsync((Guid?)null);
        var cut = RenderTab();

        cut.Find($"[data-testid='{PreviewButtonMarker}']").Click();

        Assert.That(cut.FindAll($"[data-testid='{PanelStubMarker}']"), Is.Empty);
    }

    [Test]
    public void DangerZone_RunningPreview_CannotBeStartedAgain()
    {
        _latestPreview = Preview(ActivityStatus.InProgress, ConfigurationChangePreviewStageStatus.InProgress);

        var cut = RenderTab();

        Assert.That(cut.Find($"[data-testid='{PreviewButtonMarker}']").HasAttribute("disabled"), Is.True,
            "a second run beside the first would cost a full evaluation for the same answer");
    }

    [Test]
    public void DangerZone_FinishedPreviewOvertakenByAConfigurationChange_SaysSo()
    {
        _latestPreview = Preview(ActivityStatus.Complete, ConfigurationChangePreviewStageStatus.Complete);
        _staleness = new ConfigurationChangePreviewStaleness(null, DateTime.UtcNow.AddMinutes(-2));

        var cut = RenderTab();

        Assert.That(cut.Find($"[data-testid='{StaleMarker}']").TextContent, Does.Contain("Configuration has changed"));
    }

    [Test]
    public void DangerZone_FinishedPreviewNothingSince_SaysNothingAboutStaleness()
    {
        _latestPreview = Preview(ActivityStatus.Complete, ConfigurationChangePreviewStageStatus.Complete);

        var cut = RenderTab();

        Assert.That(cut.FindAll($"[data-testid='{StaleMarker}']"), Is.Empty);
    }

    [Test]
    public void DangerZone_SystemAlreadyBeingDeleted_OffersNoPreview()
    {
        var cut = RenderTab(ConnectedSystemStatus.Deleting);

        Assert.That(cut.FindAll($"[data-testid='{PreviewButtonMarker}']"), Is.Empty);
    }

    private IRenderedComponent<ConnectedSystemDangerZoneTab> RenderTab(ConnectedSystemStatus status = ConnectedSystemStatus.Active) =>
        Render<ConnectedSystemDangerZoneTab>(p => p.Add(t => t.ConnectedSystem,
            new ConnectedSystem { Id = ConnectedSystemId, Name = "Old HR System", Status = status }));

    private static string? PanelActivityId(IRenderedComponent<ConnectedSystemDangerZoneTab> cut) =>
        cut.WaitForElement($"[data-testid='{PanelStubMarker}']").GetAttribute("data-activity-id");

    private static ConfigurationChangePreview Preview(ActivityStatus activityStatus, ConfigurationChangePreviewStageStatus stageStatus)
    {
        var activityId = Guid.CreateVersion7();
        return new ConfigurationChangePreview
        {
            ActivityId = activityId,
            Activity = new Activity { Id = activityId, Created = DateTime.UtcNow.AddMinutes(-4), Status = activityStatus, TargetType = ActivityTargetType.ConnectedSystem },
            Surface = ConfigurationChangePreviewSurface.ConnectedSystemDeletion,
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
