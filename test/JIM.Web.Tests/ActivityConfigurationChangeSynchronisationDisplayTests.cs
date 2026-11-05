// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// How an Activity's page reports the configuration changes a synchronisation applies (#2615). A synchronisation run says
/// which Object Types it processed in full and where the changes stand: applied, or (for a run that failed or was
/// cancelled) not applied, or neither while it is still going. The configuration change Activity that saved them says
/// whether a synchronisation has applied them yet, one line per Object Type.
/// </summary>
[TestFixture]
public class ActivityConfigurationChangeSynchronisationDisplayTests : JimComponentTestContext
{
    private const string Section = "[data-testid='jim-processed-in-full-section']";
    private const string Applied = "[data-testid='jim-processed-in-full-applied']";
    private const string NotApplied = "[data-testid='jim-processed-in-full-not-applied']";
    private const string GroupRow = "[data-testid='jim-change-group-row']";
    private const string GroupExpand = "[data-testid='jim-change-group-expand']";
    private const string GroupChange = "[data-testid='jim-change-group-change']";
    private const string Line = "[data-testid='jim-change-application-line']";
    private const string LineApplied = "[data-testid='jim-change-application-applied']";
    private const string LineAwaiting = "[data-testid='jim-change-application-awaiting']";
    private const string Prediction = "[data-testid='jim-change-application-prediction']";
    private const string PredictionLine = "[data-testid='jim-change-application-prediction-line']";

    // ─── A synchronisation run ───

    [Test]
    public void ProcessedInFull_OneGroupPerObjectType_RendersOneSectionEach()
    {
        var cut = RenderRun(ActivityStatus.Complete, Group("user", applied: true), Group("group", applied: true));

        Assert.That(cut.FindAll(Section), Has.Count.EqualTo(2));
    }

    [Test]
    public void ProcessedInFull_NoGroups_RendersNothing()
    {
        var cut = RenderRun(ActivityStatus.Complete);

        Assert.That(cut.FindAll(Section), Is.Empty);
    }

    [TestCase(ActivityStatus.Complete)]
    [TestCase(ActivityStatus.CompleteWithWarning)]
    [TestCase(ActivityStatus.CompleteWithError)]
    public void ProcessedInFull_RunCompletedAndChangesApplied_SaysApplied(ActivityStatus status)
    {
        var cut = RenderRun(status, Group("user", applied: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(Applied), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(NotApplied), Is.Empty);
        }
    }

    [TestCase(ActivityStatus.FailedWithError)]
    [TestCase(ActivityStatus.Cancelled)]
    public void ProcessedInFull_RunFailedOrWasCancelled_SaysNotApplied(ActivityStatus status)
    {
        var cut = RenderRun(status, Group("user", applied: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(NotApplied), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(Applied), Is.Empty);
        }
    }

    [Test]
    public void ProcessedInFull_RunStillGoing_SaysNeitherYet()
    {
        var cut = RenderRun(ActivityStatus.InProgress, Group("user", applied: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(Section), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(Applied), Is.Empty);
            Assert.That(cut.FindAll(NotApplied), Is.Empty);
        }
    }

    [Test]
    public void ProcessedInFull_TwoTypesWithDifferentOutcomes_ReportsEachOnItsOwn()
    {
        var cut = RenderRun(ActivityStatus.Complete, Group("user", applied: true), Group("group", applied: false));

        var sections = cut.FindAll(Section);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sections[0].QuerySelector(Applied), Is.Not.Null);
            Assert.That(sections[1].QuerySelector(Applied), Is.Null);
        }
    }

    [Test]
    public void ProcessedInFull_ObjectCountKnown_ShowsItWithThousandsSeparators()
    {
        var cut = RenderRun(ActivityStatus.Complete, Group("user", applied: true, objectCount: 12_500));

        Assert.That(cut.Find(Section).TextContent, Does.Contain("12,500"));
    }

    [Test]
    public void ProcessedInFull_SeveralChangesToOneRule_CollapsedIntoOneRow()
    {
        var cut = RenderRun(ActivityStatus.Complete, Group("user", applied: true, changes: 3));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll($"{Section} {GroupRow}"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll($"{Section} {GroupChange}"), Is.Empty, "the individual changes stay folded until asked for");
        }
    }

    [Test]
    public void ProcessedInFull_ExpandingARow_ListsEachChangeLinkedToItsActivity()
    {
        var cut = RenderRun(ActivityStatus.Complete, Group("user", applied: true, changes: 3));

        cut.Find($"{Section} {GroupExpand}").Click();

        var changes = cut.FindAll($"{Section} {GroupChange}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Has.Count.EqualTo(3));
            Assert.That(changes.Select(change => change.QuerySelector("a")?.GetAttribute("href")), Has.All.StartWith("/activity/"));
        }
    }

    [Test]
    public void ProcessedInFull_OneChangeToARule_NothingToExpand()
    {
        var cut = RenderRun(ActivityStatus.Complete, Group("user", applied: true, changes: 1));

        Assert.That(cut.FindAll($"{Section} {GroupExpand}"), Is.Empty);
    }

    [Test]
    public void ProcessedInFull_RunStillGoing_DescribesTheChangesInThePresentTense()
    {
        var cut = RenderRun(ActivityStatus.InProgress, ConnectedSystemRunType.FullSynchronisation, Group("user", applied: false));

        Assert.That(cut.Find(Section).TextContent, Does.Contain("also covers"));
    }

    [TestCase(ActivityStatus.Complete)]
    [TestCase(ActivityStatus.FailedWithError)]
    [TestCase(ActivityStatus.Cancelled)]
    public void ProcessedInFull_RunFinished_DescribesTheChangesInThePastTense(ActivityStatus status)
    {
        var cut = RenderRun(status, ConnectedSystemRunType.FullSynchronisation, Group("user", applied: status == ActivityStatus.Complete));

        Assert.That(cut.Find(Section).TextContent, Does.Contain("also covered"));
    }

    // ─── A configuration change Activity ───

    [Test]
    public void ChangeApplication_NoGroups_RendersNothing()
    {
        var cut = Render<ActivityConfigurationChangeApplication>(p => p.Add(c => c.Groups, []));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void ChangeApplication_OneLinePerObjectType()
    {
        var cut = RenderChange(Group("user", applied: true), Group("group", applied: false));

        Assert.That(cut.FindAll(Line), Has.Count.EqualTo(2));
    }

    [Test]
    public void ChangeApplication_Applied_LinksTheRunThatAppliedIt()
    {
        var runId = Guid.NewGuid();
        var cut = RenderChange(Group("user", applied: true, appliedBy: runId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(LineApplied), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(LineAwaiting), Is.Empty);
            Assert.That(cut.Find($"{LineApplied} a").GetAttribute("href"), Is.EqualTo($"/activity/{runId}"));
        }
    }

    [Test]
    public void ChangeApplication_AppliedByARunWhoseActivityIsGone_StillSaysApplied()
    {
        var cut = RenderChange(Group("user", applied: true, appliedBy: null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(LineApplied), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll($"{LineApplied} a"), Is.Empty, "nothing to link once the run's Activity has been removed");
        }
    }

    [Test]
    public void ChangeApplication_NotYetApplied_LinksTheConnectedSystemWhoseNextSynchronisationApplies()
    {
        var cut = RenderChange(Group("user", applied: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(LineAwaiting), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(LineApplied), Is.Empty);
            Assert.That(cut.Find($"{LineAwaiting} a").GetAttribute("href"), Is.EqualTo("/admin/connected-systems/3"));
        }
    }

    // ─── Helpers ───

    // ─── Predicted beside actual (#2618) ───

    [Test]
    public void ConfigurationChange_AppliedWithAComparison_ShowsOneLineWithPredictedAndActualPerOutcome()
    {
        var comparison = new ConfigurationChangePredictionComparison
        {
            PreviewActivityId = Guid.NewGuid(),
            Lines =
            [
                new(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, 40, 38),
                new(ActivityRunProfileExecutionItemSyncOutcomeType.Joined, 10, 12)
            ]
        };

        var cut = RenderChange(Group("user", applied: true, comparison: comparison));

        var lines = cut.FindAll(PredictionLine);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lines, Has.Count.EqualTo(2));
            Assert.That(lines[0].TextContent, Does.Contain("40").And.Contain("38"));
            Assert.That(lines[1].TextContent, Does.Contain("10").And.Contain("12"));
        }
    }

    [Test]
    public void ConfigurationChange_AppliedWithoutAComparison_ShowsNoPredictionAtAll()
    {
        var cut = RenderChange(Group("user", applied: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(LineApplied), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(Prediction), Is.Empty, "no empty column or placeholder");
        }
    }

    [Test]
    public void ConfigurationChange_AwaitingWithAComparisonSomehow_ShowsNoPrediction()
    {
        // A comparison belongs to an applied change; nothing has been done to compare against until then.
        var comparison = new ConfigurationChangePredictionComparison
        {
            PreviewActivityId = Guid.NewGuid(),
            Lines = [new(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, 40, 0)]
        };

        var cut = RenderChange(Group("user", applied: false, comparison: comparison));

        Assert.That(cut.FindAll(Prediction), Is.Empty);
    }

    private IRenderedComponent<ActivityProcessedInFullSections> RenderRun(ActivityStatus status, params ActivityObjectTypeSynchronisation[] groups) =>
        RenderRun(status, ConnectedSystemRunType.DeltaSynchronisation, groups);

    private IRenderedComponent<ActivityProcessedInFullSections> RenderRun(
        ActivityStatus status, ConnectedSystemRunType runType, params ActivityObjectTypeSynchronisation[] groups) =>
        Render<ActivityProcessedInFullSections>(p => p
            .Add(c => c.Groups, groups)
            .Add(c => c.RunStatus, status)
            .Add(c => c.RunType, runType));

    private IRenderedComponent<ActivityConfigurationChangeApplication> RenderChange(params ActivityObjectTypeSynchronisation[] groups) =>
        Render<ActivityConfigurationChangeApplication>(p => p.Add(c => c.Groups, groups));

    private static ActivityObjectTypeSynchronisation Group(
        string objectTypeName, bool applied, int changes = 1, int? objectCount = null, Guid? appliedBy = null,
        ConfigurationChangePredictionComparison? comparison = null) => new()
    {
        ConnectedSystemId = 3,
        ConnectedSystemName = "HR Import",
        ConnectedSystemObjectTypeId = objectTypeName.GetHashCode(),
        ObjectTypeName = objectTypeName,
        ObjectCount = objectCount,
        PredictionComparison = comparison,
        Changes = Enumerable.Range(1, changes).Select(i => new ConfigurationChangeSynchronisationSummary
        {
            Id = i,
            ConnectedSystemId = 3,
            ConnectedSystemName = "HR Import",
            ObjectTypeName = objectTypeName,
            Reason = $"Import {objectTypeName}: Attribute Flow changed (Sync-affecting)",
            SyncRuleId = 1,
            ConfigurationChangeActivityId = Guid.NewGuid(),
            RequestedAt = DateTime.UtcNow.AddHours(-1).AddMinutes(i),
            ClaimedByActivityId = applied ? appliedBy : null,
            AppliedAt = applied ? DateTime.UtcNow.AddMinutes(-5) : null,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "System"
        }).ToList()
    };
}
