// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Connected System delete dialog's #809 surface: the deletion-mode choice (Deprovision through
/// synchronisation pre-selected, Delete immediately behind a revealed warning), the counts regrouped by
/// fate, the reserved attribute-impact preview affordance, and the fenced-system wording for the retry and
/// finish-immediately exits. The mode choice decides whether downstream systems are corrected or left with
/// whatever the system last exported, so the default and the warning reveal are the behaviour that matters.
/// </summary>
[TestFixture]
public class DeleteConnectedSystemDialogTests : JimComponentTestContext
{
    private const int ConnectedSystemId = 7;
    private const string SystemName = "Old HR System";
    private const string ImmediateWarningMarker = "jim-delete-cs-immediate-warning";
    private const string PreviewImpactMarker = "jim-delete-cs-preview-impact";
    private const string FencedNoticeMarker = "jim-delete-cs-fenced-notice";

    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<ITaskingRepository> _mockTaskingRepo = null!;
    private JimApplication _jim = null!;
    private ConnectedSystemStatus _systemStatus = ConnectedSystemStatus.Active;

    protected override void ConfigureAdditionalServices()
    {
        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockTaskingRepo = new Mock<ITaskingRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepo.Object);

        // The dialog loads the deletion preview on initialisation; only the counts the fixture asserts on
        // need arranging (Moq's empty default provider answers the rest with completed zero-count tasks).
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(() => new ConnectedSystem { Id = ConnectedSystemId, Name = SystemName, Status = _systemStatus });
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemObjectCountAsync(ConnectedSystemId, It.IsAny<int?>()))
            .ReturnsAsync(1200);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleCountAsync(ConnectedSystemId)).ReturnsAsync(3);
        _mockConnectedSystemRepo.Setup(r => r.GetActivityCountAsync(ConnectedSystemId)).ReturnsAsync(45);
        _mockConnectedSystemRepo.Setup(r => r.GetJoinedMvoCountAsync(ConnectedSystemId)).ReturnsAsync(340);
        _mockMetaverseRepo
            .Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionCountAsync(ConnectedSystemId))
            .ReturnsAsync(28);
        _mockMetaverseRepo
            .Setup(r => r.GetContributedValueCountsByConnectedSystemAsync(ConnectedSystemId))
            .ReturnsAsync((5600, 310));

        _jim = new JimApplication(mockRepository.Object);
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_jim));
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        _systemStatus = ConnectedSystemStatus.Active;
    }

    /// <summary>
    /// Opens the dialog through the dialog service and waits for the preview-loaded state (the mode radios
    /// render only once the preview arrives), so tests never race the async initialisation.
    /// </summary>
    private IRenderedComponent<MudDialogProvider> ShowDialog()
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<DeleteConnectedSystemDialog>
        {
            { x => x.ConnectedSystem, new ConnectedSystem { Id = ConnectedSystemId, Name = SystemName, Status = _systemStatus } }
        };
        provider.InvokeAsync(() => dialogService.ShowAsync<DeleteConnectedSystemDialog>($"Delete \"{SystemName}\" Connected System", parameters));
        provider.WaitForElement("input[type='radio']");
        return provider;
    }

    /// <summary>
    /// Selects the immediate-deletion radio. MudRadio's input commits on click rather than change, so a
    /// click on the second input is what selecting it produces (ContributedValuesChoiceDialogTests
    /// precedent).
    /// </summary>
    private static void ChooseImmediate(IRenderedComponent<MudDialogProvider> provider)
    {
        var radios = provider.FindAll("input[type='radio']");
        Assert.That(radios, Has.Count.EqualTo(2), "expected exactly the deprovision and immediate radios");
        radios[1].Click();
    }

    [Test]
    public void DeleteConnectedSystemDialog_DefaultsToDeprovisionThroughSynchronisation()
    {
        var provider = ShowDialog();

        var radios = provider.FindAll("input[type='radio']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(radios, Has.Count.EqualTo(2));
            Assert.That(radios[0].HasAttribute("checked"), Is.True, "Deprovision through synchronisation must be pre-selected");
            Assert.That(provider.Markup, Does.Contain("Deprovision through synchronisation (recommended)"));
            Assert.That(provider.Markup, Does.Contain("Delete immediately and keep contributed data"));
            Assert.That(provider.FindAll($"[data-testid='{ImmediateWarningMarker}']"), Is.Empty,
                "the immediate-mode warning must not show while deprovisioning is selected");
        }
    }

    [Test]
    public void DeleteConnectedSystemDialog_ChoosingImmediate_RevealsKeepWarning()
    {
        var provider = ShowDialog();

        ChooseImmediate(provider);

        // The markup wraps the copy across source lines, so assert on its clauses rather than the whole
        // sentence with its rendered line breaks.
        var warning = provider.WaitForElement($"[data-testid='{ImmediateWarningMarker}']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.TextContent, Does.Contain("Kept values become permanently unmanaged"));
            Assert.That(warning.TextContent, Does.Contain("This cannot be reversed."));
        }
    }

    [Test]
    public void DeleteConnectedSystemDialog_ReservesDisabledPreviewImpactButton()
    {
        // The #134/#827 attribute impact preview lands later; a disabled affordance reserves its spot so
        // the layout does not shift when it does.
        var provider = ShowDialog();

        var button = provider.Find($"[data-testid='{PreviewImpactMarker}']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(button.TextContent, Does.Contain("Preview attribute impact"));
            Assert.That(button.HasAttribute("disabled"), Is.True);
        }
    }

    [Test]
    public void DeleteConnectedSystemDialog_GroupsCountsByFate()
    {
        var provider = ShowDialog();

        using (Assert.EnterMultipleScope())
        {
            // Group and subgroup headers.
            Assert.That(provider.Markup, Does.Contain("Removed with the system"));
            Assert.That(provider.Markup, Does.Contain("Affected, not removed"));
            Assert.That(provider.Markup, Does.Contain("Metaverse Objects"));

            // The affected rows and their qualifying notes.
            Assert.That(provider.Markup, Does.Contain("Joined"));
            Assert.That(provider.Markup, Does.Contain("related objects; deprovisioned per the choice above"));
            Assert.That(provider.Markup, Does.Contain("Contributed attribute values"));
            Assert.That(provider.Markup, Does.Contain("recalled or kept per the choice above"));
            Assert.That(provider.Markup, Does.Contain("Eligible for deletion rules"));
            Assert.That(provider.Markup, Does.Contain("their own rules decide, when deprovisioning"));
            Assert.That(provider.Markup, Does.Contain("Activities"));
            Assert.That(provider.Markup, Does.Contain("kept: the audit history of what this system did"));

            // The counts behind the new rows.
            Assert.That(provider.Markup, Does.Contain("5,600"), "contributed value count");
            Assert.That(provider.Markup, Does.Contain("340"), "joined Metaverse Object count");
            Assert.That(provider.Markup, Does.Contain("28"), "deletion-rule-eligible count");
        }
    }

    [Test]
    public void DeleteConnectedSystemDialog_WhenFencedWithQueuedRun_StatesRetryAttaches()
    {
        _systemStatus = ConnectedSystemStatus.Deleting;
        _mockTaskingRepo
            .Setup(r => r.GetDeleteConnectedSystemWorkerTaskAsync(ConnectedSystemId))
            .ReturnsAsync(new DeleteConnectedSystemWorkerTask
            {
                ConnectedSystemId = ConnectedSystemId,
                SynchronisedDeprovisioning = true,
                Activity = new Activity { Id = Guid.NewGuid() }
            });

        var provider = ShowDialog();

        var notice = provider.Find($"[data-testid='{FencedNoticeMarker}']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(notice.TextContent, Does.Contain("already queued or running"));
            Assert.That(notice.TextContent, Does.Contain("attaches to it"));
            Assert.That(notice.TextContent, Does.Contain("abandons the remaining deprovisioning"));
        }
    }

    [Test]
    public void DeleteConnectedSystemDialog_WhenFencedWithNoTask_StatesResumeOrFinish()
    {
        // A failed run's task row is removed at the worker's boundary, so a fenced system with no task is
        // the failed-run state: deprovisioning again resumes; immediate deletion finishes.
        _systemStatus = ConnectedSystemStatus.Deleting;
        _mockTaskingRepo
            .Setup(r => r.GetDeleteConnectedSystemWorkerTaskAsync(ConnectedSystemId))
            .ReturnsAsync((DeleteConnectedSystemWorkerTask?)null);

        var provider = ShowDialog();

        var notice = provider.Find($"[data-testid='{FencedNoticeMarker}']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(notice.TextContent, Does.Contain("stopped before completing"));
            Assert.That(notice.TextContent, Does.Contain("resumes from where the run stopped"));
            Assert.That(notice.TextContent, Does.Contain("finishes the deletion at once"));
        }
    }

    [Test]
    public void DeleteConnectedSystemDialog_WhenNotFenced_ShowsNoFencedNotice()
    {
        var provider = ShowDialog();

        Assert.That(provider.FindAll($"[data-testid='{FencedNoticeMarker}']"), Is.Empty);
    }

    /// <summary>
    /// Hands out the same, already-arranged <see cref="JimApplication"/> instance on every call; the
    /// component disposes what it creates, and the mocks behind it are stateless across calls.
    /// </summary>
    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }
}
