// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers.Preview;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Preview;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The pilot adapter (#1114): what a change to a Metaverse Object Type's deletion settings would do to the objects
/// already on their way to deletion.
///
/// The failures worth testing are the ones that would mislead an administrator into approving deletions. Reporting
/// no impact from a change that brings forward thousands of deletions is the worst of them; reporting an impact
/// from a change that moves nothing is the next worst, because a preview that cries wolf stops being read.
/// </summary>
[TestFixture]
public class MetaverseObjectTypeDeletionSettingsPreviewAdapterTests
{
    private const int ObjectTypeId = 7;
    private static readonly DateTime Now = DateTime.UtcNow;

    private Mock<IRepository> _repo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private JimApplication _jim = null!;
    private MetaverseObjectType _objectType = null!;
    private List<MetaverseObjectDeletionCandidate> _candidates = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);

        _objectType = new MetaverseObjectType
        {
            Id = ObjectTypeId,
            Name = "User",
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.FromDays(30)
        };
        _candidates = [];

        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(ObjectTypeId, It.IsAny<bool>()))
            .ReturnsAsync(() => _objectType);
        _metaverseRepo.Setup(r => r.StreamMetaverseObjectDeletionCandidates(ObjectTypeId))
            .Returns(() => _candidates.ToAsyncEnumerable());
        _metaverseRepo.Setup(r => r.GetMetaverseObjectDeletionCandidateCountAsync(ObjectTypeId))
            .ReturnsAsync(() => _candidates.Count);

        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    private MetaverseObjectTypeDeletionSettingsPreviewAdapter NewAdapter() => new(_jim);

    private PreviewContext Context(MetaverseObjectTypeDeletionSettingsProposal proposal) => new()
    {
        Surface = ConfigurationChangePreviewSurface.MetaverseObjectType,
        ActivityId = Guid.CreateVersion7(),
        TargetId = ObjectTypeId,
        ProposedConfiguration = proposal
    };

    private static MetaverseObjectTypeDeletionSettingsProposal Proposal(
        MetaverseObjectDeletionRule rule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
        TimeSpan? gracePeriod = null,
        params int[] triggerSystemIds) =>
        new(rule, gracePeriod, triggerSystemIds, AuthoritativeSourceTriggerMode.AllSourcesDisconnect);

    private void GivenCandidate(string displayName, int disconnectedDaysAgo, bool hasConnectors = false) =>
        _candidates.Add(new MetaverseObjectDeletionCandidate(Guid.CreateVersion7(), displayName,
            Now.AddDays(-disconnectedDaysAgo), hasConnectors));

    private async Task<List<PreviewDelta>> EvaluateAsync(MetaverseObjectTypeDeletionSettingsProposal proposal)
    {
        var deltas = new List<PreviewDelta>();
        await foreach (var delta in NewAdapter().EvaluateDeltasAsync(Context(proposal), CancellationToken.None))
            deltas.Add(delta);
        return deltas;
    }

    #region Stage 1: the proposal itself

    [Test]
    public async Task ValidateAsync_AuthoritativeSourceRuleWithNoTriggerSystems_IsBlockingAsync()
    {
        var proposal = Proposal(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, TimeSpan.FromDays(7));

        var findings = await NewAdapter().ValidateAsync(Context(proposal));

        Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Blocking), Is.True,
            "the rule cannot function without an authoritative source, and the engine's fallback silently behaves as a different rule");
    }

    [Test]
    public async Task ValidateAsync_TriggerSystemsChangedButNothingElse_SaysSoWithoutBlockingAsync()
    {
        // The honest answer, and it is not obvious: the trigger list is consulted at the moment a Connected System
        // Object disconnects, so editing it moves no object's deletion date today. Saying nothing here would leave
        // an empty preview looking like a broken one.
        _objectType.DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected;
        _objectType.DeletionTriggerConnectedSystemIds = [1];
        var proposal = Proposal(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            _objectType.DeletionGracePeriod, 1, 2);

        var findings = await NewAdapter().ValidateAsync(Context(proposal));

        Assert.Multiple(() =>
        {
            Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Blocking), Is.False);
            Assert.That(findings.Any(f => f.PropertyName == nameof(MetaverseObjectType.DeletionTriggerConnectedSystemIds)), Is.True);
        });
    }

    [Test]
    public async Task ValidateAsync_TriggerModeChangedButNothingElse_SaysSoWithoutBlockingAsync()
    {
        // The trigger mode (#119) is read at the same moment as the source list and has the same standing impact:
        // none. It still has to be said, or an empty preview looks like a broken one.
        _objectType.DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected;
        _objectType.DeletionTriggerConnectedSystemIds = [1];
        _objectType.DeletionTriggerMode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect;

        var findings = await NewAdapter().ValidateAsync(Context(new MetaverseObjectTypeDeletionSettingsProposal(
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            _objectType.DeletionGracePeriod,
            [1],
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect)));

        Assert.Multiple(() =>
        {
            Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Blocking), Is.False);
            Assert.That(findings.Any(f => f.PropertyName == nameof(MetaverseObjectType.DeletionTriggerMode)), Is.True);
        });
    }

    [Test]
    public async Task ValidateAsync_ValidProposal_FindsNothingBlockingAsync()
    {
        var proposal = Proposal(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.FromDays(7));

        var findings = await NewAdapter().ValidateAsync(Context(proposal));

        Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Blocking), Is.False);
    }

    #endregion

    #region Stage 2: counts

    [Test]
    public async Task CountImpactAsync_GracePeriodShortened_CountsTheObjectsItWouldDeleteNowAsync()
    {
        // 30-day grace today: nothing disconnected less than 30 days ago is eligible. Shorten it to 7 and the
        // objects between the two windows are deleted on the next housekeeping pass, minutes after saving.
        GivenCandidate("Ada", disconnectedDaysAgo: 10);
        GivenCandidate("Grace", disconnectedDaysAgo: 20);
        GivenCandidate("Katherine", disconnectedDaysAgo: 3);

        var counts = await NewAdapter().CountImpactAsync(
            Context(Proposal(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.FromDays(7))));

        var becomeEligible = counts.SingleOrDefault(c =>
            c.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible);
        Assert.That(becomeEligible, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(becomeEligible!.ObjectCount, Is.EqualTo(2));
            Assert.That(becomeEligible!.MetaverseObjectTypeId, Is.EqualTo(ObjectTypeId));
        });
    }

    [Test]
    public async Task CountImpactAsync_RuleSetToManual_SeparatesDeletionsCancelledFromDeletionsRescheduledAsync()
    {
        GivenCandidate("Ada", disconnectedDaysAgo: 40);   // already eligible under the 30-day grace
        GivenCandidate("Grace", disconnectedDaysAgo: 10); // still waiting

        var counts = await NewAdapter().CountImpactAsync(
            Context(Proposal(MetaverseObjectDeletionRule.Manual, _objectType.DeletionGracePeriod)));

        Assert.Multiple(() =>
        {
            Assert.That(counts.Single(c => c.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible).ObjectCount,
                Is.EqualTo(1), "one object is being deleted today and would not be");
            Assert.That(counts.Single(c => c.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate).ObjectCount,
                Is.EqualTo(1), "the other is not being deleted today either way, but comes off the path entirely");
        });
    }

    [Test]
    public async Task CountImpactAsync_NoSettingChanged_ReportsNothingAsync()
    {
        GivenCandidate("Ada", disconnectedDaysAgo: 40);

        var counts = await NewAdapter().CountImpactAsync(
            Context(Proposal(_objectType.DeletionRule, _objectType.DeletionGracePeriod)));

        Assert.That(counts, Is.Empty, "a proposal that changes neither setting cannot move any object's fate.");
        _metaverseRepo.Verify(r => r.StreamMetaverseObjectDeletionCandidates(It.IsAny<int>()), Times.Never,
            "and it must reach that answer without reading the population at all.");
    }

    [Test]
    public async Task CountImpactAsync_GracePeriodEditedBetweenNoneAndZero_ReportsNothingAsync()
    {
        // The housekeeping sweep reads an absent grace period and a zero one identically, so an edit between them
        // changes nothing. Treating them as different would fill a preview with rows whose before and after dates
        // are the same, and teach an administrator that the preview cries wolf.
        _objectType.DeletionGracePeriod = null;
        GivenCandidate("Ada", disconnectedDaysAgo: 40);

        var counts = await NewAdapter().CountImpactAsync(Context(Proposal(_objectType.DeletionRule, TimeSpan.Zero)));

        Assert.That(counts, Is.Empty);
        _metaverseRepo.Verify(r => r.StreamMetaverseObjectDeletionCandidates(It.IsAny<int>()), Times.Never,
            "the two are the same setting, so the population should not be read to discover that nothing moved.");
    }

    #endregion

    #region Stages 3 and 4: per-object deltas

    [Test]
    public async Task EvaluateDeltasAsync_SwitchToAuthoritativeSourceRule_MakesStillConnectedObjectsEligibleAsync()
    {
        // The transition an administrator is least likely to predict: the authoritative-source rule deletes an
        // object whose trigger system has gone even though other systems still hold it, so switching to it can
        // delete objects that the last-connector rule was deliberately keeping.
        GivenCandidate("Ada", disconnectedDaysAgo: 40, hasConnectors: true);

        var deltas = await EvaluateAsync(Proposal(
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, _objectType.DeletionGracePeriod, 1));

        Assert.That(deltas, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(deltas[0].TransitionType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible));
            Assert.That(deltas[0].ObjectDisplayName, Is.EqualTo("Ada"));
            Assert.That(deltas[0].ObjectTypeName, Is.EqualTo("User"));
            Assert.That(deltas[0].MetaverseObjectTypeId, Is.EqualTo(ObjectTypeId));
            Assert.That(deltas[0].OldValue, Is.Null, "the object has no deletion date today; that is the point");
            Assert.That(deltas[0].NewValue, Is.Not.Null);
        });
    }

    [Test]
    public async Task EvaluateDeltasAsync_ObjectWhoseFateDoesNotMove_YieldsNothingAsync()
    {
        // Its deletion date is the disconnection date under both settings, because neither has a grace period.
        _objectType.DeletionGracePeriod = null;
        GivenCandidate("Ada", disconnectedDaysAgo: 40);

        var deltas = await EvaluateAsync(Proposal(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, null, 1));

        Assert.That(deltas, Is.Empty,
            "the rule changed but this object is deleted on the same date either way; a delta here would be noise.");
    }

    [Test]
    public async Task EvaluateDeltasAsync_GracePeriodLengthened_NamesTheSettingThatMovedTheDateAsync()
    {
        GivenCandidate("Ada", disconnectedDaysAgo: 10);

        var deltas = await EvaluateAsync(Proposal(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.FromDays(60)));

        Assert.That(deltas, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(deltas[0].TransitionType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate));
            // Grouping is by the setting that caused the change, so a summary reads "12,400 objects, Deletion Grace
            // Period" rather than 12,400 rows of distinct dates.
            Assert.That(deltas[0].AttributeName, Is.EqualTo("Deletion Grace Period"));
            Assert.That(deltas[0].OldValue, Is.Not.Null);
            Assert.That(deltas[0].NewValue, Is.Not.Null);
            Assert.That(deltas[0].OldValue, Is.Not.EqualTo(deltas[0].NewValue));
        });
    }

    [Test]
    public async Task EvaluateDeltasAsync_BothSettingsChanged_NamesBothAsync()
    {
        GivenCandidate("Ada", disconnectedDaysAgo: 10, hasConnectors: true);

        var deltas = await EvaluateAsync(Proposal(
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, TimeSpan.FromDays(1), 1));

        Assert.That(deltas, Has.Count.EqualTo(1));
        Assert.That(deltas[0].AttributeName, Is.EqualTo("Deletion Rule and Deletion Grace Period"));
    }

    [Test]
    public void EvaluateDeltasAsync_Cancelled_StopsEvaluating()
    {
        GivenCandidate("Ada", disconnectedDaysAgo: 40);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in NewAdapter().EvaluateDeltasAsync(
                Context(Proposal(MetaverseObjectDeletionRule.Manual, _objectType.DeletionGracePeriod)), cancellation.Token))
            {
                // Drained deliberately without inspecting anything: the assertion is that the stream throws rather
                // than what it yields, and an administrator who cancels must not leave an evaluation running.
            }
        });
    }

    #endregion

    #region Cost estimate

    [Test]
    public async Task EstimateCostAsync_CountsOnlyTheMarkedObjects_NotTheWholePopulationAsync()
    {
        GivenCandidate("Ada", disconnectedDaysAgo: 40);
        GivenCandidate("Grace", disconnectedDaysAgo: 10);

        var estimate = await NewAdapter().EstimateCostAsync(
            Context(Proposal(MetaverseObjectDeletionRule.Manual, _objectType.DeletionGracePeriod)));

        Assert.Multiple(() =>
        {
            Assert.That(estimate.AffectedObjects, Is.EqualTo(2));
            Assert.That(estimate.EstimatedDeltaRows, Is.EqualTo(2), "this adapter emits at most one delta per object");
        });
    }

    #endregion

    [Test]
    public void Adapter_ProposalOfTheWrongType_FailsLoudly()
    {
        var context = new PreviewContext
        {
            Surface = ConfigurationChangePreviewSurface.MetaverseObjectType,
            ActivityId = Guid.CreateVersion7(),
            TargetId = ObjectTypeId,
            ProposedConfiguration = "not a deletion settings proposal"
        };

        Assert.ThrowsAsync<InvalidOperationException>(() => NewAdapter().ValidateAsync(context),
            "a preview that evaluated the wrong shape would answer confidently about a change nobody proposed.");
    }
}
