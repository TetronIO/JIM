// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Scheduling;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Extensions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Schedule editor's failure handling (#1787): the Schedule's "When a step fails" setting on the Steps tab, the
/// three-way setting in the step editor, and the indicator on every step row saying what that step does when it fails
/// and where that comes from. The indicator is resolved live from the editor's in-memory Schedule, so changing the
/// Schedule's setting must change every step that follows it before anything is saved.
/// </summary>
[TestFixture]
public class ScheduleEditorDialogTests : JimComponentTestContext
{
    private const string IndicatorMarker = "jim-step-failure-behaviour";
    private const int StepsTabIndex = 2;

    private Mock<ISchedulingRepository> _schedulingRepository = null!;
    private JimApplication _jim = null!;
    private Schedule _schedule = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _schedulingRepository = new Mock<ISchedulingRepository>();
        var connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        repository.Setup(r => r.Scheduling).Returns(_schedulingRepository.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(connectedSystemRepository.Object);

        connectedSystemRepository.Setup(r => r.GetConnectedSystemHeadersAsync()).ReturnsAsync(
        [
            new ConnectedSystemHeader { Id = 1, Name = "HR", ConnectorName = "SQL Connector" },
            new ConnectedSystemHeader { Id = 2, Name = "Active Directory", ConnectorName = "LDAP Connector" }
        ]);
        connectedSystemRepository.Setup(r => r.GetConnectedSystemRunProfilesAsync(1)).ReturnsAsync(
        [
            new ConnectedSystemRunProfile { Id = 11, Name = "Full Import" },
            new ConnectedSystemRunProfile { Id = 12, Name = "Full Synchronisation" }
        ]);
        connectedSystemRepository.Setup(r => r.GetConnectedSystemRunProfilesAsync(2)).ReturnsAsync(
        [
            new ConnectedSystemRunProfile { Id = 21, Name = "Export" },
            new ConnectedSystemRunProfile { Id = 22, Name = "Delta Import" }
        ]);
        _schedulingRepository.Setup(r => r.GetScheduleWithStepsAsync(It.IsAny<Guid>())).ReturnsAsync(() => _schedule);

        _jim = new JimApplication(repository.Object);
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_jim));
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await DisposeComponentsAsync();
    }

    private static ScheduleStep Step(int stepIndex, int connectedSystemId, int runProfileId, ScheduleStepFailureBehaviour onFailure,
        StepExecutionMode mode = StepExecutionMode.Sequential) => new()
    {
        Id = Guid.NewGuid(),
        StepIndex = stepIndex,
        StepType = ScheduleStepType.RunProfile,
        ExecutionMode = mode,
        ConnectedSystemId = connectedSystemId,
        RunProfileId = runProfileId,
        OnFailure = onFailure
    };

    private static Schedule NightlyHrSync(ScheduleFailureBehaviour onStepFailure, bool builtIn = false, params ScheduleStep[] steps)
    {
        var schedule = new Schedule
        {
            Id = Guid.NewGuid(),
            Name = "Nightly HR sync",
            TriggerType = ScheduleTriggerType.Manual,
            OnStepFailure = onStepFailure,
            BuiltIn = builtIn
        };
        schedule.Steps.AddRange(steps);
        return schedule;
    }

    /// <summary>
    /// Opens the editor for <see cref="_schedule"/> and switches to its Steps tab, waiting out the async load so no
    /// test races the dialog's initialisation.
    /// </summary>
    private IRenderedComponent<MudDialogProvider> OpenStepsTab()
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<ScheduleEditorDialog> { { x => x.ScheduleId, _schedule.Id } };
        provider.InvokeAsync(() => dialogService.ShowAsync<ScheduleEditorDialog>("Edit Schedule", parameters));
        provider.WaitForState(() => provider.FindComponents<MudTabs>().Count > 0);

        var tabs = provider.FindComponent<MudTabs>();
        provider.InvokeAsync(() => tabs.Instance.ActivatePanelAsync(StepsTabIndex, false));
        provider.WaitForState(() => provider.FindComponents<MudSelect<ScheduleFailureBehaviour>>().Count > 0);
        return provider;
    }

    private static List<string> Indicators(IRenderedComponent<MudDialogProvider> provider) =>
        provider.FindAll($"[data-testid='{IndicatorMarker}']").Select(e => string.Join(" ", e.TextContent.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries))).ToList();

    [Test]
    public void StepList_EveryStepIncludingParallelMembers_ShowsItsEffectiveBehaviourAndSource()
    {
        _schedule = NightlyHrSync(ScheduleFailureBehaviour.Continue, false,
            Step(0, 1, 11, ScheduleStepFailureBehaviour.FollowSchedule),
            Step(1, 2, 21, ScheduleStepFailureBehaviour.Stop),
            Step(2, 1, 12, ScheduleStepFailureBehaviour.FollowSchedule),
            Step(2, 2, 22, ScheduleStepFailureBehaviour.Stop, StepExecutionMode.ParallelWithPrevious));

        var provider = OpenStepsTab();

        Assert.That(Indicators(provider), Is.EqualTo(new[]
        {
            "Continues on failure From the Schedule",
            "Stops on failure Set on this step",
            "Continues on failure From the Schedule",
            "Stops on failure Set on this step"
        }));
    }

    [Test]
    public void ScheduleSetting_Changed_UpdatesEveryFollowingStepBeforeSaving()
    {
        _schedule = NightlyHrSync(ScheduleFailureBehaviour.Stop, false,
            Step(0, 1, 11, ScheduleStepFailureBehaviour.FollowSchedule),
            Step(1, 2, 21, ScheduleStepFailureBehaviour.Stop));
        var provider = OpenStepsTab();
        Assert.That(Indicators(provider)[0], Is.EqualTo("Stops on failure From the Schedule"));

        var select = provider.FindComponent<MudSelect<ScheduleFailureBehaviour>>();
        provider.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(ScheduleFailureBehaviour.Continue));

        provider.WaitForAssertion(() => Assert.That(Indicators(provider), Is.EqualTo(new[]
        {
            "Continues on failure From the Schedule",
            "Stops on failure Set on this step"
        })));
    }

    [Test]
    public void ScheduleSetting_UserSchedule_IsEditableAndDefaultsFromTheSchedule()
    {
        _schedule = NightlyHrSync(ScheduleFailureBehaviour.Continue, false, Step(0, 1, 11, ScheduleStepFailureBehaviour.FollowSchedule));

        var provider = OpenStepsTab();

        var select = provider.FindComponent<MudSelect<ScheduleFailureBehaviour>>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(select.Instance.Label, Is.EqualTo("When a step fails"));
            Assert.That(select.Instance.GetState(x => x.Value), Is.EqualTo(ScheduleFailureBehaviour.Continue));
            Assert.That(select.Instance.Disabled, Is.False);
        }
    }

    [Test]
    public void ScheduleSetting_BuiltInSchedule_IsFixedAndSaysJimManagesIt()
    {
        _schedule = NightlyHrSync(ScheduleFailureBehaviour.Stop, true, Step(0, 1, 11, ScheduleStepFailureBehaviour.FollowSchedule));

        var provider = OpenStepsTab();

        var select = provider.FindComponent<MudSelect<ScheduleFailureBehaviour>>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(select.Instance.Disabled, Is.True);
            Assert.That(select.Instance.HelperText, Does.Contain("JIM manages"));
            Assert.That(Indicators(provider), Is.EqualTo(new[] { "Stops on failure From the Schedule" }));
        }
    }

    [Test]
    public void AddStep_NewStep_FollowsTheScheduleAndSaysWhatThatCurrentlyMeans()
    {
        _schedule = NightlyHrSync(ScheduleFailureBehaviour.Continue, false, Step(0, 1, 11, ScheduleStepFailureBehaviour.FollowSchedule));
        var provider = OpenStepsTab();

        provider.FindAll("button").First(b => b.TextContent.Contains("Add Step")).Click();
        provider.WaitForState(() => provider.FindComponents<MudSelect<ScheduleStepFailureBehaviour>>().Count > 0);

        var stepSelect = provider.FindComponent<MudSelect<ScheduleStepFailureBehaviour>>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stepSelect.Instance.Label, Is.EqualTo("When this step fails"));
            Assert.That(stepSelect.Instance.GetState(x => x.Value), Is.EqualTo(ScheduleStepFailureBehaviour.FollowSchedule));
            Assert.That(stepSelect.Instance.ToStringFunc!(ScheduleStepFailureBehaviour.FollowSchedule),
                Is.EqualTo("Follow the Schedule (currently: continue)"));
        }
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }
}
