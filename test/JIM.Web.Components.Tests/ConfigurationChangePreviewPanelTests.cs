// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Models.Utility;
using JIM.Web.Services;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Components.Tests;

/// <summary>
/// The panel is the only thing most administrators will ever see of a preview, so the failures worth testing are the
/// ones where it renders something an administrator would reasonably act on and be wrong:
///
/// A preview that failed part-way has evaluated an arbitrary subset of the population. Its groups and counts are
/// real numbers over a fraction of the objects, and presenting them the way a finished preview presents them is how
/// a destructive change gets approved on the strength of the half that happened to be evaluated.
///
/// A capped group's drill-down is a sample. Unlabelled, a thousand harmless-looking rows read as "these are all the
/// objects affected", when the group's own count says there are forty thousand.
///
/// And a preview still running is not a preview that found nothing. An empty summary during evaluation must not be
/// rendered as an answer.
/// </summary>
[TestFixture]
// A fresh bUnit context per test. Unlike the dialog fixtures beside it, this one registers its own services in
// SetUp, and a bUnit service provider refuses further registrations once anything has resolved from it: under
// NUnit's default single-instance lifecycle every test after the first would fail in SetUp rather than in its body.
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class ConfigurationChangePreviewPanelTests : JimComponentTestContext
{
    private static readonly Guid ActivityId = Guid.CreateVersion7();

    private Mock<IRepository> _repository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConfigurationChangePreviewRepository> _previewRepository = null!;
    private FakeUiNotificationService _notifications = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        _previewRepository = new Mock<IConfigurationChangePreviewRepository>();
        _repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _repository.Setup(r => r.ConfigurationChangePreviews).Returns(_previewRepository.Object);
        _repository.Setup(r => r.Tasking).Returns(new Mock<ITaskingRepository>().Object);
        _previewRepository.Setup(r => r.GetPreviewGroupsAsync(It.IsAny<Guid>())).ReturnsAsync([]);
        _previewRepository
            .Setup(r => r.GetPreviewDeltasAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync(new PagedResultSet<ConfigurationChangePreviewDelta>());

        _notifications = new FakeUiNotificationService();
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_repository.Object));
        Services.AddSingleton<IUiNotificationService>(_notifications);
        Services.AddSingleton<IUserPreferenceService>(new Mock<IUserPreferenceService>().Object);
    }

    [Test]
    public void Panel_FailedPreview_SaysSoAndWithholdsTheGroupsItDidRecord()
    {
        // The groups exist and their counts are arithmetically correct; they are just not an answer to the question
        // the administrator asked, because the evaluation stopped part-way through the population.
        GivenPreview(p =>
        {
            p.ValidationStatus = ConfigurationChangePreviewStageStatus.Complete;
            p.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Complete;
            p.SummaryStatus = ConfigurationChangePreviewStageStatus.Failed;
            p.DeltasStatus = ConfigurationChangePreviewStageStatus.Failed;
        }, a =>
        {
            a.Status = ActivityStatus.FailedWithError;
            a.ErrorMessage = "the evaluation query timed out";
        });
        GivenGroups(Group(4_812));

        var panel = RenderPanel();

        Assert.Multiple(() =>
        {
            Assert.That(panel.Markup, Does.Contain("the evaluation query timed out"),
                "the administrator needs to know why, not just that");
            Assert.That(panel.Markup, Does.Not.Contain("4,812"),
                "a count drawn from a partial evaluation must not be presented as what the change would do");
        });
    }

    [Test]
    public void Panel_RunningPreviewWithNoGroupsYet_DoesNotReadAsNothingToDo()
    {
        GivenPreview(p =>
        {
            p.ValidationStatus = ConfigurationChangePreviewStageStatus.Complete;
            p.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Complete;
            p.SummaryStatus = ConfigurationChangePreviewStageStatus.InProgress;
            p.DeltasStatus = ConfigurationChangePreviewStageStatus.InProgress;
        }, a =>
        {
            a.Status = ActivityStatus.InProgress;
            a.Message = "Evaluating what the change would do";
        });

        var panel = RenderPanel();

        Assert.Multiple(() =>
        {
            Assert.That(panel.Markup, Does.Contain("Evaluating what the change would do"));
            Assert.That(panel.Markup, Does.Not.Contain("would not change anything"),
                "an evaluation in progress has not concluded that nothing would change");
        });
    }

    [Test]
    public void Panel_CompletePreviewWithNoGroups_SaysNothingWouldChange()
    {
        // The opposite case, and the reason the one above matters: here the answer genuinely is "nothing", and
        // saying so plainly is the whole value of having asked.
        GivenPreview(Complete);

        var panel = RenderPanel();

        Assert.That(panel.Markup, Does.Contain("would not change anything"));
    }

    [Test]
    public void Panel_CountOnlyAdapter_DoesNotClaimNothingWouldChange()
    {
        // Stages 3 and 4 not applicable means the adapter never looked at individual objects. An empty summary here
        // is an absence of evidence, and rendering it as "nothing would change" would invent a finding.
        GivenPreview(p =>
        {
            p.ValidationStatus = ConfigurationChangePreviewStageStatus.Complete;
            p.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Complete;
            p.SummaryStatus = ConfigurationChangePreviewStageStatus.NotApplicable;
            p.DeltasStatus = ConfigurationChangePreviewStageStatus.NotApplicable;
            p.ImpactCounts = """[{"TransitionType":22,"ObjectCount":4812,"ConnectedSystemId":null,"MetaverseObjectTypeId":11}]""";
        });

        var panel = RenderPanel();

        Assert.Multiple(() =>
        {
            Assert.That(panel.Markup, Does.Contain("4,812"), "the counts it did produce are the answer it has");
            Assert.That(panel.Markup, Does.Not.Contain("would not change anything"));
        });
    }

    [Test]
    public void Panel_CappedGroup_LabelsItsDrillDownAsASample()
    {
        GivenPreview(p =>
        {
            Complete(p);
            p.DeltaPersistence = ConfigurationChangePreviewDeltaPersistence.Capped;
        });
        GivenGroups(Group(40_000, sampled: true));

        var panel = RenderPanel();

        Assert.Multiple(() =>
        {
            Assert.That(panel.Markup, Does.Contain("40,000"), "the count is exact whether or not the rows were capped");
            Assert.That(panel.Markup, Does.Contain("sample").IgnoreCase,
                "an unlabelled sample read as a complete list is the failure this label exists to prevent");
        });
    }

    [Test]
    public void Panel_UncappedGroup_IsNotLabelledAsASample()
    {
        GivenPreview(Complete);
        GivenGroups(Group(12));

        var panel = RenderPanel();

        Assert.That(panel.Markup, Does.Not.Contain("sample").IgnoreCase,
            "labelling a complete list as a sample teaches the label to be ignored where it matters");
    }

    [Test]
    public void Panel_BlockingValidationFinding_LeadsWithIt()
    {
        GivenPreview(p =>
        {
            p.ValidationStatus = ConfigurationChangePreviewStageStatus.Complete;
            p.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.NotApplicable;
            p.SummaryStatus = ConfigurationChangePreviewStageStatus.NotApplicable;
            p.DeltasStatus = ConfigurationChangePreviewStageStatus.NotApplicable;
            p.ValidationFindings = """[{"Severity":2,"Message":"No deletion triggers are selected.","PropertyName":"DeletionTriggers"}]""";
        }, a => a.Status = ActivityStatus.CompleteWithWarning);

        var panel = RenderPanel();

        Assert.Multiple(() =>
        {
            Assert.That(panel.Markup, Does.Contain("No deletion triggers are selected."));
            Assert.That(panel.Markup, Does.Contain("cannot be applied"),
                "a blocking finding stops the change, and the panel has to say that rather than only listing it");
        });
    }

    [Test]
    public void Panel_MalformedStoredFindings_StillRendersTheRestOfThePreview()
    {
        GivenPreview(p =>
        {
            Complete(p);
            p.ValidationFindings = "not json";
        });
        GivenGroups(Group(7));

        var panel = RenderPanel();

        Assert.That(panel.Markup, Does.Contain("7"),
            "an unreadable findings document should not cost the administrator the summary beside it");
    }

    [Test]
    public void Panel_RunningPreview_OffersCancel()
    {
        GivenPreview(p => p.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.InProgress,
            a => a.Status = ActivityStatus.InProgress);

        var panel = RenderPanel();

        Assert.That(panel.FindAll("[data-testid='jim-preview-cancel']"), Is.Not.Empty);
    }

    [Test]
    public void Panel_FinishedPreview_DoesNotOfferCancel()
    {
        GivenPreview(Complete);

        var panel = RenderPanel();

        Assert.That(panel.FindAll("[data-testid='jim-preview-cancel']"), Is.Empty,
            "offering to cancel something that has already finished only produces a confusing 'nothing to cancel'");
    }

    [Test]
    public void Panel_ActivityProgressNotificationForItsOwnPreview_RequeriesRatherThanTrustingTheHint()
    {
        // Notifications carry no data; the whole contract is that a subscriber re-reads. A panel that rendered from
        // the notification would show whatever it had last, for ever.
        GivenPreview(p => p.SummaryStatus = ConfigurationChangePreviewStageStatus.InProgress,
            a => a.Status = ActivityStatus.InProgress);
        var panel = RenderPanel();
        var readsBefore = ReadCount();

        panel.InvokeAsync(() => _notifications.RaiseActivityProgressChanged(ActivityId));
        panel.WaitForState(() => ReadCount() > readsBefore, TimeSpan.FromSeconds(2));

        Assert.That(ReadCount(), Is.GreaterThan(readsBefore));
    }

    [Test]
    public void Panel_ActivityProgressNotificationForAnotherActivity_IsIgnored()
    {
        GivenPreview(p => p.SummaryStatus = ConfigurationChangePreviewStageStatus.InProgress,
            a => a.Status = ActivityStatus.InProgress);
        var panel = RenderPanel();
        var readsBefore = ReadCount();

        panel.InvokeAsync(() => _notifications.RaiseActivityProgressChanged(Guid.CreateVersion7()));

        Assert.That(ReadCount(), Is.EqualTo(readsBefore),
            "a busy system raises these constantly; a panel that re-read on every one would hammer the database");
    }

    [Test]
    public void Panel_UnknownPreview_SaysItIsNotThereRatherThanRenderingAnEmptyResult()
    {
        // What an administrator sees once retention has removed a preview, or if they follow a stale link. An empty
        // panel would read as "this change would do nothing".
        _previewRepository.Setup(r => r.GetPreviewAsync(ActivityId)).ReturnsAsync((ConfigurationChangePreview?)null);

        var panel = RenderPanel();

        Assert.Multiple(() =>
        {
            Assert.That(panel.Markup, Does.Contain("no longer available").IgnoreCase);
            Assert.That(panel.Markup, Does.Not.Contain("would not change anything"));
        });
    }

    #region Helpers

    private IRenderedComponent<ConfigurationChangePreviewPanel> RenderPanel()
    {
        var panel = Render<ConfigurationChangePreviewPanel>(p => p.Add(x => x.ActivityId, ActivityId));
        panel.WaitForState(() => !panel.Markup.Contains("jim-preview-loading"), TimeSpan.FromSeconds(2));
        return panel;
    }

    private int ReadCount() => _previewRepository.Invocations.Count(i => i.Method.Name == nameof(IConfigurationChangePreviewRepository.GetPreviewAsync));

    private static void Complete(ConfigurationChangePreview preview)
    {
        preview.ValidationStatus = ConfigurationChangePreviewStageStatus.Complete;
        preview.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Complete;
        preview.SummaryStatus = ConfigurationChangePreviewStageStatus.Complete;
        preview.DeltasStatus = ConfigurationChangePreviewStageStatus.Complete;
    }

    private void GivenPreview(Action<ConfigurationChangePreview>? configurePreview = null, Action<Activity>? configureActivity = null)
    {
        var preview = new ConfigurationChangePreview
        {
            ActivityId = ActivityId,
            Surface = ConfigurationChangePreviewSurface.MetaverseObjectType
        };
        configurePreview?.Invoke(preview);

        var activity = new Activity
        {
            Id = ActivityId,
            TargetType = ActivityTargetType.MetaverseObjectType,
            TargetOperationType = ActivityTargetOperationType.Preview,
            TargetName = "User",
            Status = ActivityStatus.Complete
        };
        configureActivity?.Invoke(activity);

        _previewRepository.Setup(r => r.GetPreviewAsync(ActivityId)).ReturnsAsync(preview);
        _activityRepository.Setup(r => r.GetActivityAsync(ActivityId)).ReturnsAsync(activity);
    }

    private void GivenGroups(params ConfigurationChangePreviewGroup[] groups) =>
        _previewRepository.Setup(r => r.GetPreviewGroupsAsync(ActivityId)).ReturnsAsync([.. groups]);

    private static ConfigurationChangePreviewGroup Group(int objectCount, bool sampled = false) => new()
    {
        Id = Guid.CreateVersion7(),
        ActivityId = ActivityId,
        TransitionType = ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible,
        MetaverseObjectTypeId = 11,
        MetaverseObjectTypeName = "User",
        ObjectCount = objectCount,
        DeltasSampled = sampled
    };

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }

    private sealed class FakeUiNotificationService : IUiNotificationService
    {
        public event Action<JIM.Models.Tasking.WorkerTaskChangeNotification>? WorkerTaskChanged;

        public event Action<Guid>? ActivityProgressChanged;

        public event Action<bool>? RealTimeAvailabilityChanged;

        public bool IsRealTimeAvailable => true;

        public void RaiseActivityProgressChanged(Guid activityId) => ActivityProgressChanged?.Invoke(activityId);

        public void RaiseRealTimeAvailabilityChanged(bool available) => RealTimeAvailabilityChanged?.Invoke(available);

        // Declared by the interface; the panel does not subscribe to it, and referencing it here keeps the compiler
        // from warning about an event that is never raised.
        public void RaiseWorkerTaskChanged(JIM.Models.Tasking.WorkerTaskChangeNotification notification) =>
            WorkerTaskChanged?.Invoke(notification);
    }

    #endregion
}
