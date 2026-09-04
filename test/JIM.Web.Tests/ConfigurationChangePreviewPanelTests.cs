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

namespace JIM.Web.Tests;

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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(panel.Markup, Does.Contain("the evaluation query timed out"),
                "the administrator needs to know why, not just that");
            Assert.That(panel.Markup, Does.Not.Contain("4,812"),
                "a count drawn from a partial evaluation must not be presented as what the change would do");
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(panel.Markup, Does.Contain("Evaluating what the change would do"));
            Assert.That(panel.Markup, Does.Not.Contain("would not change anything"),
                "an evaluation in progress has not concluded that nothing would change");
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(panel.Markup, Does.Contain("4,812"), "the counts it did produce are the answer it has");
            Assert.That(panel.Markup, Does.Not.Contain("would not change anything"));
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(panel.Markup, Does.Contain("40,000"), "the count is exact whether or not the rows were capped");
            Assert.That(panel.Markup, Does.Contain("sample").IgnoreCase,
                "an unlabelled sample read as a complete list is the failure this label exists to prevent");
        }
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
    public void Panel_SummaryRow_SaysItCoversObjectsOfTheType()
    {
        // A summary row is about many objects, so "User in Yellowstone Verify" described one of them. The type name
        // itself stays exactly as its system spells it, because it is a schema identifier and not JIM's to inflect;
        // "objects" after it is what carries the plurality (#1275).
        GivenPreview(Complete);
        GivenGroups(Group(4_812));

        var panel = RenderPanel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(panel.Markup, Does.Contain("User"));
            Assert.That(panel.Markup, Does.Contain("objects"));
            Assert.That(panel.Markup, Does.Not.Contain("Users"),
                "pluralising the type name would have JIM inventing a name the system it came from does not use");
        }
    }

    [Test]
    public void Panel_GroupNamingAValuePair_ShowsBothValues()
    {
        GivenPreview(Complete);
        GivenGroups(Group(38_900, attributeName: "Email", oldValue: "@contoso.com", newValue: "@fabrikam.com"));

        var panel = RenderPanel();

        using (Assert.EnterMultipleScope())
        {
            // "38,900 would have Email changed" is a summary of the wrong thing; the values are what makes it
            // reviewable without opening the drill-down at all.
            Assert.That(panel.Markup, Does.Contain("@contoso.com"));
            Assert.That(panel.Markup, Does.Contain("@fabrikam.com"));
            Assert.That(panel.Markup, Does.Contain("Email"));
        }
    }

    [Test]
    public void Panel_GroupWithNoValuePair_ShowsTheAttributeAlone()
    {
        GivenPreview(Complete);
        GivenGroups(Group(38_900, attributeName: "Email"));

        var panel = RenderPanel();

        // A group that collapsed past the cardinality guard covers many values, so the row must not imply one.
        Assert.That(panel.Markup, Does.Contain("Email"));
        Assert.That(panel.Markup, Does.Not.Contain("→"), "the arrow only makes sense between two values");
    }

    [Test]
    public void Panel_GroupWithADetectedPattern_SaysWhatKindOfChangeItIs()
    {
        GivenPreview(Complete);
        GivenGroups(Group(38_900, attributeName: "Email", patternKey: PreviewPatternKeys.EmailDomainChanged));

        var panel = RenderPanel();

        // The point of Phase 4b: a collapsed group covering thousands of distinct value pairs is unreadable as
        // values, and entirely readable as "they are all domain changes".
        Assert.That(panel.Markup, Does.Contain("Email or UPN domain changed"));
    }

    [Test]
    public void Panel_GroupWithNoDetectedPattern_ShowsNothingInItsPlace()
    {
        GivenPreview(Complete);
        GivenGroups(Group(38_900, attributeName: "Email"));

        var panel = RenderPanel();

        Assert.That(panel.Markup, Does.Not.Contain("domain changed"),
            "no detector recognised this change, and a blank is the honest rendering of that");
    }

    [Test]
    public void Panel_GroupWithAPatternThisBuildDoesNotKnow_ShowsNothingRatherThanTheRawKey()
    {
        GivenPreview(Complete);
        GivenGroups(Group(12, attributeName: "Email", patternKey: "SomethingElseEntirely"));

        var panel = RenderPanel();

        Assert.That(panel.Markup, Does.Not.Contain("SomethingElseEntirely"),
            "an internal identifier is not something to put in front of an administrator");
    }

    [Test]
    public void Panel_DrillDownOnOneOfSeveralValuePairGroups_NamesWhichOneIsOpen()
    {
        // Value-pair grouping puts several rows on screen that share a transition and a population and differ only
        // in their values. A heading that names the transition alone identifies none of them.
        GivenPreview(Complete);
        GivenGroups(
            Group(38_900, attributeName: "Email", oldValue: "@contoso.com", newValue: "@fabrikam.com"),
            Group(1_650, attributeName: "Email", oldValue: "@contoso.co.uk", newValue: "@fabrikam.co.uk"));

        var panel = RenderPanel();
        OpenSummaryRowContaining(panel, "@contoso.co.uk");

        var heading = panel.Find("[data-testid='jim-preview-drilldown-heading']").TextContent;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(heading, Does.Contain("@contoso.co.uk"));
            Assert.That(heading, Does.Contain("@fabrikam.co.uk"));
            Assert.That(heading, Does.Contain("Email"));
        }
    }

    [Test]
    public void Panel_DrillDownRowsWithDifferentPatterns_LabelsEachOne()
    {
        // A group that collapsed past the value-pair guard carries rows of more than one kind, so the group itself
        // is unlabelled and the rows are where the distinction survives.
        GivenPreview(Complete);
        GivenGroups(Group(2, attributeName: "Email"));
        GivenDeltas(
            Delta("bob@contoso.com", "bob@fabrikam.com", PreviewPatternKeys.EmailDomainChanged),
            Delta("bsmith", "svc-bsmith", PreviewPatternKeys.PrefixAdded));

        var panel = RenderPanel();
        OpenSummaryRowContaining(panel, "Email");
        panel.WaitForState(() => panel.Markup.Contains("Prefix added"), TimeSpan.FromSeconds(2));

        Assert.That(panel.Markup, Does.Contain("Email or UPN domain changed"));
    }

    [Test]
    public void Panel_DrillDown_ReadsTheStoredDeltasFromTheFirstPageNotPageZero()
    {
        // The object-level rows are stored behind a page-based read while the grid asks for arbitrary windows, so the
        // panel serves a window from the whole pages that cover it. That conversion is 1-based on the way out: a
        // page zero is silently accepted by the repository and comes back holding the wrong rows, which is a defect
        // no rendering assertion would catch, because the panel would still show a full and plausible list.
        GivenPreview(Complete);
        GivenGroups(Group(2, attributeName: "Email"));
        GivenDeltas(Delta("bob@contoso.com", "bob@fabrikam.com", PreviewPatternKeys.EmailDomainChanged));

        var panel = RenderPanel();
        OpenSummaryRowContaining(panel, "Email");
        panel.WaitForState(() => panel.Markup.Contains("bob@fabrikam.com"), TimeSpan.FromSeconds(2));

        _previewRepository.Verify(
            r => r.GetPreviewDeltasAsync(ActivityId, It.IsAny<Guid?>(), It.Is<int>(page => page >= 1), It.IsAny<int>(), It.IsAny<string?>()),
            Times.AtLeastOnce);
        _previewRepository.Verify(
            r => r.GetPreviewDeltasAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.Is<int>(page => page < 1), It.IsAny<int>(), It.IsAny<string?>()),
            Times.Never, "the stored read is 1-based; a page below one reads the wrong rows without failing");
    }

    [Test]
    public void Panel_DrillDownWithNoStoredDetail_SaysDetailWasNotKeptRatherThanBlamingASearch()
    {
        // The two reasons a drill-down is empty mean opposite things: a search that matched nothing is the reader's
        // to undo, while a preview that kept no object-level detail is a property of how the preview was run and has
        // no way out. Showing the search message when nothing was searched sends the reader looking for a filter.
        GivenPreview(Complete);
        GivenGroups(Group(2, attributeName: "Email"));
        GivenDeltas();

        var panel = RenderPanel();
        OpenSummaryRowContaining(panel, "Email");
        panel.WaitForState(() => panel.Markup.Contains("No object-level detail was kept"), TimeSpan.FromSeconds(2));

        Assert.That(panel.Markup, Does.Not.Contain("match that search"),
            "nothing was searched, so the empty state must not offer clearing a search as the way out");
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(panel.Markup, Does.Contain("No deletion triggers are selected."));
            Assert.That(panel.Markup, Does.Contain("cannot be applied"),
                "a blocking finding stops the change, and the panel has to say that rather than only listing it");
        }
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
    public void Panel_WithACloseHandler_OffersCloseAndRaisesIt()
    {
        // The panel does not decide what closing means; the surface that opened it does (the Synchronisation Rule
        // editor forgets the preview, so the save confirmation stops citing it). The panel's part is the affordance.
        GivenPreview(Complete);
        var closed = false;

        var panel = Render<ConfigurationChangePreviewPanel>(p => p
            .Add(x => x.ActivityId, ActivityId)
            .Add(x => x.OnClose, () => closed = true));
        panel.WaitForState(() => !panel.Markup.Contains("jim-preview-loading"), TimeSpan.FromSeconds(2));

        panel.Find("[data-testid='jim-preview-close']").Click();

        Assert.That(closed, Is.True);
    }

    [Test]
    public void Panel_WithoutACloseHandler_DoesNotOfferClose()
    {
        // The Activity page shows a preview as the record of what was evaluated; there is nothing to close there,
        // and a button that did nothing would be worse than none.
        GivenPreview(Complete);

        var panel = RenderPanel();

        Assert.That(panel.FindAll("[data-testid='jim-preview-close']"), Is.Empty);
    }

    [Test]
    public void Panel_RunningPreview_OffersCloseBesideCancel()
    {
        // Closing puts the panel away; cancelling stops the evaluation. Both stand while a preview runs, so an
        // administrator who has seen enough is not made to choose between waiting and stopping it.
        GivenPreview(p => p.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.InProgress,
            a => a.Status = ActivityStatus.InProgress);

        var panel = Render<ConfigurationChangePreviewPanel>(p => p
            .Add(x => x.ActivityId, ActivityId)
            .Add(x => x.OnClose, () => { }));
        panel.WaitForState(() => !panel.Markup.Contains("jim-preview-loading"), TimeSpan.FromSeconds(2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(panel.FindAll("[data-testid='jim-preview-cancel']"), Is.Not.Empty);
            Assert.That(panel.FindAll("[data-testid='jim-preview-close']"), Is.Not.Empty);
        }
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
    public void Panel_ProgressSuppressed_LeavesProgressToItsHostButKeepsCancel()
    {
        // The Activity detail page already renders this Activity's progress, message and ETA above the panel. A
        // second bar underneath is noise; the ability to stop the thing is not, and that page has no cancel of its
        // own.
        GivenPreview(p => p.SummaryStatus = ConfigurationChangePreviewStageStatus.InProgress, a =>
        {
            a.Status = ActivityStatus.InProgress;
            a.Message = "Evaluating what the change would do";
            a.ObjectsToProcess = 100;
            a.ObjectsProcessed = 40;
        });

        var panel = Render<ConfigurationChangePreviewPanel>(p => p
            .Add(x => x.ActivityId, ActivityId)
            .Add(x => x.ShowProgress, false));
        panel.WaitForState(() => !panel.Markup.Contains("jim-preview-loading"), TimeSpan.FromSeconds(2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(panel.Markup, Does.Not.Contain("Evaluating what the change would do"));
            Assert.That(panel.FindAll("[data-testid='jim-preview-cancel']"), Is.Not.Empty);
            Assert.That(panel.Markup, Does.Contain("Summary"), "the stages are the panel's own and stay either way");
        }
    }

    [Test]
    public void Panel_UnknownPreview_SaysItIsNotThereRatherThanRenderingAnEmptyResult()
    {
        // What an administrator sees once retention has removed a preview, or if they follow a stale link. An empty
        // panel would read as "this change would do nothing".
        _previewRepository.Setup(r => r.GetPreviewAsync(ActivityId)).ReturnsAsync((ConfigurationChangePreview?)null);

        var panel = RenderPanel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(panel.Markup, Does.Contain("no longer available").IgnoreCase);
            Assert.That(panel.Markup, Does.Not.Contain("would not change anything"));
        }
    }

    [Test]
    public void Panel_HostReRendersWithTheSamePreview_DoesNotReRead()
    {
        // Found by driving the portal (#1114): a host that binds OnPreviewChanged re-renders when the callback
        // fires, because that is what an EventCallback does. A panel that re-read on every parameter set turned
        // that into a loop, and the loop cancelled and restarted the reconciliation poll on each pass, so the
        // poll's delay never elapsed. The panel sat on stage 1 for ever while the preview finished behind it.
        GivenPreview(p => p.ValidationStatus = ConfigurationChangePreviewStageStatus.Complete,
            a => a.Status = ActivityStatus.InProgress);
        var panel = RenderPanel();
        var readsAfterFirstLoad = ReadCount();

        panel.Render();
        panel.Render();

        Assert.That(ReadCount(), Is.EqualTo(readsAfterFirstLoad),
            "a parent re-render is not new information about the preview; the notification handler and the poll are what refresh it");
    }

    [Test]
    public void Panel_PointedAtADifferentPreview_LoadsIt()
    {
        GivenPreview();
        var panel = RenderPanel();
        var otherActivityId = Guid.CreateVersion7();
        _previewRepository.Setup(r => r.GetPreviewAsync(otherActivityId)).ReturnsAsync((ConfigurationChangePreview?)null);
        var readsAfterFirstLoad = ReadCount();

        panel.Render(p => p.Add(x => x.ActivityId, otherActivityId));

        Assert.That(ReadCount(), Is.GreaterThan(readsAfterFirstLoad),
            "the guard is about the same preview, not about never reading again; re-previewing must load the new one");
    }

    #region Helpers

    private IRenderedComponent<ConfigurationChangePreviewPanel> RenderPanel()
    {
        var panel = Render<ConfigurationChangePreviewPanel>(p => p.Add(x => x.ActivityId, ActivityId));
        panel.WaitForState(() => !panel.Markup.Contains("jim-preview-loading"), TimeSpan.FromSeconds(2));
        return panel;
    }

    private int ReadCount() => _previewRepository.Invocations.Count(i => i.Method.Name == nameof(IConfigurationChangePreviewRepository.GetPreviewAsync));

    /// <summary>
    /// Opens the drill-down for the summary row whose text contains <paramref name="text"/>.
    /// <para>
    /// Rows are picked by what they say rather than by position, because the summary is a virtualised grid: its
    /// tbody carries the virtualiser's two zero-height spacer rows around the real ones, and those have no click
    /// handler, so the first and last positions are not rows at all. Clicking a cell is enough; the row's own
    /// handler catches the bubbled event.
    /// </para>
    /// </summary>
    private static void OpenSummaryRowContaining(IRenderedComponent<ConfigurationChangePreviewPanel> panel, string text)
    {
        var cell = panel.FindAll("tbody td").FirstOrDefault(td => td.TextContent.Contains(text, StringComparison.Ordinal));
        Assert.That(cell, Is.Not.Null, $"no summary row containing \"{text}\" was rendered");
        cell!.Click();
    }

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

    private void GivenDeltas(params ConfigurationChangePreviewDelta[] deltas) =>
        _previewRepository
            .Setup(r => r.GetPreviewDeltasAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync(new PagedResultSet<ConfigurationChangePreviewDelta>
            {
                Results = [.. deltas],
                TotalResults = deltas.Length,
                CurrentPage = 1,
                PageSize = 25
            });

    private static ConfigurationChangePreviewDelta Delta(string oldValue, string newValue, string? patternKey) => new()
    {
        Id = Guid.CreateVersion7(),
        ActivityId = ActivityId,
        TransitionType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
        ObjectDisplayName = "Bob Smith",
        ObjectTypeName = "User",
        AttributeName = "Email",
        OldValue = oldValue,
        NewValue = newValue,
        PatternKey = patternKey
    };

    private void GivenGroups(params ConfigurationChangePreviewGroup[] groups) =>
        _previewRepository.Setup(r => r.GetPreviewGroupsAsync(ActivityId)).ReturnsAsync([.. groups]);

    private static ConfigurationChangePreviewGroup Group(int objectCount, bool sampled = false,
        string? attributeName = null, string? oldValue = null, string? newValue = null,
        string? patternKey = null) => new()
    {
        Id = Guid.CreateVersion7(),
        ActivityId = ActivityId,
        TransitionType = ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible,
        MetaverseObjectTypeId = 11,
        MetaverseObjectTypeName = "User",
        AttributeName = attributeName,
        OldValue = oldValue,
        NewValue = newValue,
        PatternKey = patternKey,
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
