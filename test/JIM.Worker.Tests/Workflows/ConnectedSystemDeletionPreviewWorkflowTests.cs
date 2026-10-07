// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// The Connected System deletion impact preview (#134): a read-only dry run of Synchronised Deprovisioning (#809)
/// that reports, per object, what deleting the system would do. Each test sets up a state the deprovisioning
/// workflow tests also exercise, asks the preview about it, and checks the answer names the consequence the real
/// run produces there. The preview runs the same obsoletion core as the real run, so these tests are about the
/// classification of its results and about the preview touching nothing.
/// </summary>
[TestFixture]
public class ConnectedSystemDeletionPreviewWorkflowTests : SynchronisedDeprovisioningTestBase
{
    // -----------------------------------------------------------------------------------------------------------------
    // Values cleared and taken over
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task PreviewSynchronisedDeprovisioningAsync_SoleContributor_ReportsClearedValuesAndTheirExportsAsync()
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        var targetCso = SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);
        var mvo = SyncRepo.MetaverseObjects.Values.Single();

        var deltas = await PreviewAsync(ctx.Hr);

        var cleared = Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor);
        var exports = Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared.Select(d => d.AttributeName), Is.EquivalentTo(new[] { "DisplayName", "EmployeeId", "Description" }),
                "every attribute HR alone contributes must be reported cleared");
            Assert.That(cleared.All(d => d.MetaverseObjectId == mvo.Id && d.NewValue == null), Is.True);
            Assert.That(cleared.Select(d => d.ConnectedSystemId), Is.All.Null,
                "a cleared value happens on the Metaverse Object, so it names no Connected System to group or label it under");
            Assert.That(cleared.Single(d => d.AttributeName == "Description").OldValue, Is.EqualTo(HrDescription));

            Assert.That(exports.Select(d => d.AttributeName), Is.EquivalentTo(new[] { "DisplayName", "Description" }),
                "both exported attributes are cleared in the target");
            Assert.That(exports.All(d => d.ConnectedSystemId == ctx.Target.Id && d.ConnectedSystemObjectId == targetCso.Id && d.NewValue == null), Is.True);
            Assert.That(exports.Single(d => d.AttributeName == "Description").OldValue, Is.EqualTo(HrDescription),
                "the old side is what the target holds now");

            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible), Is.Empty,
                "the target's object is still a connector, so the Metaverse Object stays");
        }
    }

    [Test]
    public async Task PreviewSynchronisedDeprovisioningAsync_SurvivingContributorWithDifferentValue_ReportsTakeoverAsync()
    {
        var ctx = await SetUpTwoContributorsWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);
        var targetCso = SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        var deltas = await PreviewAsync(ctx.Hr);

        var takeover = Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverContributedValue).SingleOrDefault();
        var descriptionExport = Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport)
            .SingleOrDefault(d => d.AttributeName == "Description");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(takeover, Is.Not.Null, "Training takes Description over with its own value");
            Assert.That(takeover?.AttributeName, Is.EqualTo("Description"));
            Assert.That(takeover?.OldValue, Is.EqualTo(HrDescription));
            Assert.That(takeover?.NewValue, Is.EqualTo(TrainingDescription));
            Assert.That(takeover?.ConnectedSystemId, Is.EqualTo(ctx.Training!.Id), "the delta names the new contributor");

            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor).Select(d => d.AttributeName),
                Is.EquivalentTo(new[] { "DisplayName", "EmployeeId" }), "only what Training does not contribute is cleared");

            Assert.That(descriptionExport?.NewValue, Is.EqualTo(TrainingDescription),
                "the target receives the new contributor's value");
            Assert.That(descriptionExport?.ConnectedSystemObjectId, Is.EqualTo(targetCso.Id));
        }
    }

    [Test]
    public async Task PreviewSynchronisedDeprovisioningAsync_SurvivingContributor_LeavesTheSurvivorAsItFoundItAsync()
    {
        // The takeover re-flows Training's object against the preview's copy of the Metaverse Object (#1899). That object
        // is the repository's own instance, change-tracked on the worker's context, so it must not be left bound to the
        // copy for a later save there to find.
        var ctx = await SetUpTwoContributorsWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);
        var training = SyncRepo.ConnectedSystemObjects.Values.Single(cso => cso.ConnectedSystemId == ctx.Training!.Id);
        var trainingLinkBefore = training.MetaverseObject;

        var deltas = await PreviewAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverContributedValue), Is.Not.Empty,
                "arrange: Training takes Description over");
            Assert.That(training.MetaverseObject, Is.SameAs(trainingLinkBefore));
        }
    }

    [Test]
    public async Task PreviewSynchronisedDeprovisioningAsync_SurvivingContributorWithSameValue_ReportsQuietTakeoverAndNoExportAsync()
    {
        var ctx = await SetUpTwoContributorsWithExportTargetAsync(trainingDescription: HrDescription);
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        var deltas = await PreviewAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverSameValue).Select(d => d.AttributeName),
                Is.EqualTo(new[] { "Description" }), "only the source changes");
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverContributedValue), Is.Empty);
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport).Any(d => d.AttributeName == "Description"),
                Is.False, "an unchanged value exports nothing");
        }
    }

    [Test]
    public async Task PreviewSynchronisedDeprovisioningAsync_RecallDisabledObjectType_ReportsValuesKeptAndNothingClearedAsync()
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync();
        var hrType = SyncRepo.ConnectedSystemObjects.Values.First(c => c.ConnectedSystemId == ctx.Hr.Id).Type;
        hrType.RemoveContributedAttributesOnObsoletion = false;
        await RunFullSyncAsync(ctx.Hr);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);
        var mvo = SyncRepo.MetaverseObjects.Values.Single();

        var deltas = await PreviewAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues).Select(d => d.MetaverseObjectId),
                Is.EqualTo(new Guid?[] { mvo.Id }), "the object's contributed values are kept by policy");
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor), Is.Empty);
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport), Is.Empty);
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Deletion eligibility
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task PreviewSynchronisedDeprovisioningAsync_LastConnectorImmediateRule_ReportsEligibilityOnlyAsync()
    {
        var (hrSystem, _, _, _, _) = await SetUpHrContributorAsync();
        await RunFullSyncAsync(hrSystem);
        var mvo = SyncRepo.MetaverseObjects.Values.Single();

        var deltas = await PreviewAsync(hrSystem);

        var eligibility = Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible).SingleOrDefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(eligibility?.MetaverseObjectId, Is.EqualTo(mvo.Id));
            Assert.That(eligibility?.NewValue, Is.EqualTo("Eligible for deletion immediately"));
            Assert.That(eligibility?.ConnectedSystemId, Is.Null, "eligibility is the Metaverse Object's, not any system's");
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor), Is.Empty,
                "an object deleted outright has nothing recalled first");
        }
    }

    [Test]
    public async Task PreviewSynchronisedDeprovisioningAsync_GracePeriodRule_ReportsEligibilityAndFrozenValuesStayAsync()
    {
        var (hrSystem, _, mvType, _, _) = await SetUpHrContributorAsync();
        await RunFullSyncAsync(hrSystem);
        mvType.DeletionGracePeriod = TimeSpan.FromDays(7);

        var deltas = await PreviewAsync(hrSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible).SingleOrDefault()?.NewValue,
                Does.StartWith("Eligible for deletion after"));
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor), Is.Empty,
                "values are frozen for the grace window, not cleared");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Leaving an export rule's scope
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task PreviewSynchronisedDeprovisioningAsync_RecallTakesObjectOutOfExportScope_ReportsRemovalFromTargetAsync()
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync(exportScopedOnDescription: true);
        await RunFullSyncAsync(ctx.Hr);
        var targetCso = SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        var deltas = await PreviewAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport).Select(d => d.ConnectedSystemObjectId),
                Is.EqualTo(new Guid?[] { targetCso.Id }), "the target leaves the rule's scope and the rule deletes what leaves it");
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport), Is.Empty,
                "an object leaving scope is removed, not updated");
        }
    }

    [Test]
    public async Task PreviewSynchronisedDeprovisioningAsync_RecallTakesObjectOutOfExportScopeWithDisconnectAction_ReportsDisconnectionAsync()
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync(exportScopedOnDescription: true,
            scopeExitAction: OutboundDeprovisionAction.Disconnect);
        await RunFullSyncAsync(ctx.Hr);
        var targetCso = SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        var deltas = await PreviewAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject).Select(d => d.ConnectedSystemObjectId),
                Is.EqualTo(new Guid?[] { targetCso.Id }));
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible), Has.Count.EqualTo(1),
                "the disconnected target was the object's last connector, so it becomes eligible for deletion");
        }
    }

    [Test]
    public async Task PreviewSynchronisedDeprovisioningAsync_ObjectWhoseNameTheDeletionClears_IsNamedAsItStandsTodayAsync()
    {
        // HR is the object's only source of its name, so the deletion clears it. Every row about the object must still
        // name it as the administrator knows it now: naming it from the deletion's end state showed its id instead, on
        // exactly the rows that matter most (its account removed downstream, itself eligible for deletion).
        var ctx = await SetUpSoleContributorWithExportTargetAsync(exportScopedOnDescription: true,
            scopeExitAction: OutboundDeprovisionAction.Disconnect);
        await RunFullSyncAsync(ctx.Hr);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);
        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        // The stored name, as synchronisation keeps it; applying a change recomputes it from the values that remain, which
        // is how the deletion's end state loses it.
        mvo.CachedDisplayName = "John Smith";

        var deltas = await PreviewAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject), Is.Not.Empty,
                "the fixture must reach the downstream row for this to prove anything");
            Assert.That(deltas.Select(d => d.ObjectDisplayName).Distinct(), Is.EqualTo(new[] { "John Smith" }));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // The residue pass
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task PreviewSynchronisedDeprovisioningAsync_StrandedValue_ReportsItClearedAsync()
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);
        var strandedMvo = SeedStrandedMetaverseObject(ctx);

        var deltas = await PreviewAsync(ctx.Hr);

        var strandedClear = Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor)
            .SingleOrDefault(d => d.MetaverseObjectId == strandedMvo.Id);
        Assert.That(strandedClear?.OldValue, Is.EqualTo("Stranded value"),
            "a value whose object no longer holds one of the system's objects is recalled by provenance");
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Touches nothing
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task PreviewSynchronisedDeprovisioningAsync_AnyState_ChangesNothingAsync()
    {
        var ctx = await SetUpTwoContributorsWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);
        SeedStrandedMetaverseObject(ctx);
        var before = Snapshot();

        var deltas = await PreviewAsync(ctx.Hr);

        Assert.That(deltas, Is.Not.Empty, "precondition: the preview found something to report");
        Assert.That(Snapshot(), Is.EqualTo(before), "a preview must leave every object, value, join and export as it found them");
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Preview equals execution: each state is previewed, then really deprovisioned, and the facts the preview reported
    // must be exactly the facts the deletion produced. This is the guarantee the preview exists to give, enforced by a
    // test rather than by both sides calling one core.
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task PreviewEqualsExecution_SoleContributorAsync()
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        await AssertPreviewEqualsExecutionAsync(ctx.Hr);
    }

    [Test]
    public async Task PreviewEqualsExecution_SurvivingContributorWithDifferentValueAsync()
    {
        var ctx = await SetUpTwoContributorsWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        await AssertPreviewEqualsExecutionAsync(ctx.Hr);
    }

    [Test]
    public async Task PreviewEqualsExecution_SurvivingContributorWithSameValueAsync()
    {
        var ctx = await SetUpTwoContributorsWithExportTargetAsync(trainingDescription: HrDescription);
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        await AssertPreviewEqualsExecutionAsync(ctx.Hr);
    }

    [Test]
    public async Task PreviewEqualsExecution_RecallDisabledObjectTypeAsync()
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync();
        SyncRepo.ConnectedSystemObjects.Values.First(c => c.ConnectedSystemId == ctx.Hr.Id).Type.RemoveContributedAttributesOnObsoletion = false;
        await RunFullSyncAsync(ctx.Hr);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        await AssertPreviewEqualsExecutionAsync(ctx.Hr);
    }

    [TestCase(OutboundDeprovisionAction.Delete)]
    [TestCase(OutboundDeprovisionAction.Disconnect)]
    public async Task PreviewEqualsExecution_RecallTakesObjectOutOfExportScopeAsync(OutboundDeprovisionAction action)
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync(exportScopedOnDescription: true, scopeExitAction: action);
        await RunFullSyncAsync(ctx.Hr);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        await AssertPreviewEqualsExecutionAsync(ctx.Hr);
    }

    [TestCase(0)]
    [TestCase(7)]
    public async Task PreviewEqualsExecution_LastConnectorAsync(int gracePeriodDays)
    {
        var (hrSystem, _, mvType, _, _) = await SetUpHrContributorAsync();
        await RunFullSyncAsync(hrSystem);
        if (gracePeriodDays > 0)
            mvType.DeletionGracePeriod = TimeSpan.FromDays(gracePeriodDays);

        await AssertPreviewEqualsExecutionAsync(hrSystem);
    }

    [Test]
    public async Task PreviewEqualsExecution_AuthoritativeSourceDeletionCascadesDownstreamAsync()
    {
        // The Metaverse Object is deleted immediately because its authoritative source leaves, although the target's
        // object is still joined, so the deletion's cascade deprovisions that object downstream.
        var ctx = await SetUpSoleContributorWithExportTargetAsync(exportScopedOnDescription: false);
        await RunFullSyncAsync(ctx.Hr);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);
        var mvType = SyncRepo.MetaverseObjects.Values.Single().Type!;
        mvType.DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected;
        mvType.DeletionTriggerConnectedSystemIds = [ctx.Hr.Id];
        foreach (var rule in SyncRepo.SyncRules.Values.Where(r => r.ConnectedSystemId == ctx.Target.Id))
            rule.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;

        var facts = await AssertPreviewEqualsExecutionAsync(ctx.Hr);

        Assert.That(facts, Has.Some.Matches<string>(f => f.StartsWith("export-delete|")),
            "precondition: the scenario must actually exercise the downstream cascade");
    }

    [Test]
    public async Task PreviewEqualsExecution_StrandedValueAsync()
    {
        var ctx = await SetUpTwoContributorsWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);
        SeedStrandedMetaverseObject(ctx);

        await AssertPreviewEqualsExecutionAsync(ctx.Hr);
    }

    [TestCase(true, TestName = "PreviewEqualsExecution_ReferenceTakenOverByAnotherContributor_ExportsTheNewReferenceAsync")]
    [TestCase(false, TestName = "PreviewEqualsExecution_ReferenceWithNoOtherContributor_ExportsItsRemovalAsync")]
    public async Task PreviewEqualsExecution_ReferenceRecallAsync(bool trainingMentorsJohn)
    {
        // A reference recalled by the deletion reaches the target as a reference to the new referent's object there, or
        // as its removal; and the referent HR alone knew (Mary) is still exported to, so the update must resolve.
        var topology = await SetUpManagerReferenceWithExportTargetAsync(trainingMentorsJohn);
        await RunFullSyncAsync(topology.Hr);
        await RunFullSyncAsync(topology.Training);
        ApplyStagedExports(topology.Target);

        var facts = await AssertPreviewEqualsExecutionAsync(topology.Hr);

        Assert.That(facts, Has.Some.Matches<string>(f => f.StartsWith("export-update|") && f.Contains("|Manager|")),
            "precondition: the scenario must actually export the recalled reference");
    }

    /// <summary>
    /// Previews deleting the system, then really deprovisions it, and asserts the two describe the same facts. Returns
    /// the facts, so a test can also check its scenario exercised what it set out to.
    /// </summary>
    private async Task<HashSet<string>> AssertPreviewEqualsExecutionAsync(ConnectedSystem connectedSystem)
    {
        var before = CaptureObservableState();
        var previewed = FactsFromPreview(await PreviewAsync(connectedSystem));

        SyncRepo.ClearAllPendingExports();
        var (task, _) = await FenceSystemAndBuildTaskAsync(connectedSystem);
        await Jim.ConnectedSystems.ExecuteSynchronisedDeprovisioningAsync(task);
        var executed = FactsFromExecution(before, CaptureObservableState(), connectedSystem.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(previewed.Except(executed), Is.Empty, "the preview reported something the deletion did not do");
            Assert.That(executed.Except(previewed), Is.Empty, "the deletion did something the preview did not report");
        }
        return executed;
    }

    private static HashSet<string> FactsFromPreview(IEnumerable<PreviewDelta> deltas)
    {
        var facts = new HashSet<string>();
        foreach (var delta in deltas)
        {
            switch (delta.TransitionType)
            {
                case ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor:
                    facts.Add($"cleared|{delta.MetaverseObjectId}|{delta.AttributeName}");
                    break;
                case ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverContributedValue:
                    facts.Add($"changed|{delta.MetaverseObjectId}|{delta.AttributeName}|{delta.NewValue}");
                    facts.Add($"source|{delta.MetaverseObjectId}|{delta.AttributeName}|{delta.ConnectedSystemId}");
                    break;
                case ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverSameValue:
                    facts.Add($"source|{delta.MetaverseObjectId}|{delta.AttributeName}|{delta.ConnectedSystemId}");
                    break;
                case ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible:
                    facts.Add($"eligible|{delta.MetaverseObjectId}");
                    break;
                case ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues:
                    facts.Add($"kept|{delta.MetaverseObjectId}");
                    break;
                case ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport:
                    facts.Add($"export-update|{delta.ConnectedSystemObjectId}|{delta.AttributeName}|{delta.NewValue}");
                    break;
                case ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport:
                    facts.Add($"export-delete|{delta.ConnectedSystemObjectId}");
                    break;
                case ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject:
                    facts.Add($"disconnect|{delta.ConnectedSystemObjectId}");
                    break;
                default:
                    facts.Add($"unexpected|{delta.TransitionType}|{delta.MetaverseObjectId}|{delta.AttributeName}");
                    break;
            }
        }
        return facts;
    }

    private HashSet<string> FactsFromExecution(ObservableState before, ObservableState after, int deletedSystemId)
    {
        var facts = new HashSet<string>();

        foreach (var (mvoId, beforeObject) in before.MetaverseObjects)
        {
            if (!after.MetaverseObjects.TryGetValue(mvoId, out var afterObject))
            {
                facts.Add($"eligible|{mvoId}");
                continue;
            }

            // Marked for deletion: by the deletion rule (values frozen for the grace window, so they read as kept and are
            // not a separate fact), or by a scope exit disconnecting its last connector after its values were recalled.
            var markedForDeletion = afterObject.LastConnectorDisconnectedDate.HasValue && !beforeObject.LastConnectorDisconnectedDate.HasValue;
            if (markedForDeletion)
                facts.Add($"eligible|{mvoId}");

            foreach (var attributeName in beforeObject.Values
                         .Where(v => v.ContributedBySystemId == deletedSystemId)
                         .Select(v => v.AttributeName)
                         .Distinct())
            {
                var beforeValues = beforeObject.Values.Where(v => v.AttributeName == attributeName).ToList();
                var afterValues = afterObject.Values.Where(v => v.AttributeName == attributeName).ToList();

                if (afterValues.Count == 0)
                {
                    facts.Add($"cleared|{mvoId}|{attributeName}");
                    continue;
                }

                var beforeText = JIM.Application.Servers.Preview.PreviewValueRenderer.Join(beforeValues.Select(v => v.Value));
                var afterText = JIM.Application.Servers.Preview.PreviewValueRenderer.Join(afterValues.Select(v => v.Value));
                var newSource = afterValues.Select(v => v.ContributedBySystemId).FirstOrDefault(id => id.HasValue);

                if (newSource.HasValue && newSource != deletedSystemId)
                {
                    if (afterText != beforeText)
                        facts.Add($"changed|{mvoId}|{attributeName}|{afterText}");
                    facts.Add($"source|{mvoId}|{attributeName}|{newSource}");
                }
                else if (!markedForDeletion)
                {
                    facts.Add($"kept|{mvoId}");
                }
            }
        }

        var attributeNames = DbContext.ConnectedSystemObjectTypes.SelectMany(t => t.Attributes).ToDictionary(a => a.Id, a => a.Name);
        foreach (var pendingExport in SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId != deletedSystemId))
        {
            if (pendingExport.ChangeType == PendingExportChangeType.Delete)
            {
                facts.Add($"export-delete|{pendingExport.ConnectedSystemObjectId}");
                continue;
            }

            foreach (var changes in pendingExport.AttributeValueChanges.GroupBy(c => c.AttributeId))
            {
                var name = changes.Select(c => c.Attribute?.Name).FirstOrDefault(n => n != null) ?? attributeNames.GetValueOrDefault(changes.Key);
                var value = JIM.Application.Servers.Preview.PreviewValueRenderer.Join(changes.Select(JIM.Application.Servers.Preview.PreviewValueRenderer.Render));
                facts.Add($"export-update|{pendingExport.ConnectedSystemObjectId}|{name}|{value}");
            }
        }

        // A disconnection that comes with a Delete is one consequence, the removal, as the preview reports it: deleting a
        // Metaverse Object both stages its objects' deletion and breaks their joins.
        var deletedCsoIds = SyncRepo.PendingExports.Values
            .Where(pe => pe.ChangeType == PendingExportChangeType.Delete && pe.ConnectedSystemObjectId.HasValue)
            .Select(pe => pe.ConnectedSystemObjectId!.Value)
            .ToHashSet();
        foreach (var (csoId, joinedTo) in before.Joins.Where(j => j.Value.ConnectedSystemId != deletedSystemId && !deletedCsoIds.Contains(j.Key)))
        {
            var stillThere = after.Joins.TryGetValue(csoId, out var afterJoin);
            if (stillThere && afterJoin!.MetaverseObjectId == null && joinedTo.MetaverseObjectId != null)
                facts.Add($"disconnect|{csoId}");
        }

        return facts;
    }

    private sealed record ObservedValue(string? AttributeName, string? Value, int? ContributedBySystemId);

    private sealed record ObservedMetaverseObject(DateTime? LastConnectorDisconnectedDate, List<ObservedValue> Values);

    private sealed record ObservedJoin(int ConnectedSystemId, Guid? MetaverseObjectId);

    private sealed record ObservableState(Dictionary<Guid, ObservedMetaverseObject> MetaverseObjects, Dictionary<Guid, ObservedJoin> Joins);

    /// <summary>
    /// A detached copy of what a deletion can change, so the before and after states can be compared once the same
    /// instances have been mutated.
    /// </summary>
    private ObservableState CaptureObservableState() => new(
        SyncRepo.MetaverseObjects.Values.ToDictionary(
            m => m.Id,
            m => new ObservedMetaverseObject(m.LastConnectorDisconnectedDate, m.AttributeValues
                .Where(v => !v.NullValue)
                .Select(v => new ObservedValue(v.Attribute?.Name, JIM.Application.Servers.Preview.PreviewValueRenderer.Render(v), v.ContributedBySystemId))
                .ToList())),
        SyncRepo.ConnectedSystemObjects.Values.ToDictionary(c => c.Id, c => new ObservedJoin(c.ConnectedSystemId, c.MetaverseObjectId)));

    // -----------------------------------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------------------------------

    private sealed record ManagerReferenceTopology(ConnectedSystem Hr, ConnectedSystem Training, ConnectedSystem Target);

    /// <summary>
    /// HR projects Mary, Bob and John and contributes John's Manager (Mary) at priority 1; Training joins Bob and John
    /// on EmployeeId and, when <paramref name="trainingMentorsJohn"/>, contributes John's Mentor (Bob) to Manager at
    /// priority 2. A target provisions every person with DisplayName and Manager.
    /// </summary>
    private async Task<ManagerReferenceTopology> SetUpManagerReferenceWithExportTargetAsync(bool trainingMentorsJohn)
    {
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var hrExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var hrDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var hrEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var hrManagerAttr = new ConnectedSystemObjectTypeAttribute { Name = "Manager", Type = AttributeDataType.Reference, Selected = true };
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser",
            new List<ConnectedSystemObjectTypeAttribute> { hrExternalIdAttr, hrDisplayNameAttr, hrEmployeeIdAttr, hrManagerAttr });

        var trainingSystem = await CreateConnectedSystemAsync("Training Source");
        var trainingExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var trainingEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var trainingMentorAttr = new ConnectedSystemObjectTypeAttribute { Name = "Mentor", Type = AttributeDataType.Reference, Selected = true };
        var trainingType = await CreateCsoTypeAsync(trainingSystem.Id, "TrainingRecord",
            new List<ConnectedSystemObjectTypeAttribute> { trainingExternalIdAttr, trainingEmployeeIdAttr, trainingMentorAttr });

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvManagerAttr = new MetaverseAttribute
        {
            Name = "Manager",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvManagerAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvManagerAttr);

        var hrImportRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDisplayNameAttr, hrDisplayNameAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvEmployeeIdAttr, hrEmployeeIdAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvManagerAttr, hrManagerAttr, priority: 1));

        var trainingImportRule = await CreateImportSyncRuleAsync(trainingSystem.Id, trainingType, mvType, "Training Import", enableProjection: false);
        trainingImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(trainingImportRule, mvManagerAttr, trainingMentorAttr, priority: 2));
        trainingImportRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = trainingImportRule,
            SyncRuleId = trainingImportRule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new() { Order = 0, ConnectedSystemAttribute = trainingEmployeeIdAttr, ConnectedSystemAttributeId = trainingEmployeeIdAttr.Id }
            }
        });
        await DbContext.SaveChangesAsync();

        var hrMaryCso = await CreateCsoAsync(hrSystem.Id, hrType, "Mary Manager", "EMP002");
        await CreateCsoAsync(hrSystem.Id, hrType, "Bob Mentor", "EMP003");
        var hrJohnCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", SharedEmployeeId);
        hrJohnCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrManagerAttr.Id, Attribute = hrManagerAttr,
            ReferenceValueId = hrMaryCso.Id, ReferenceValue = hrMaryCso, ConnectedSystemObject = hrJohnCso
        });

        var trainingBobCso = await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", "EMP003");
        var trainingJohnCso = await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", SharedEmployeeId);
        if (trainingMentorsJohn)
        {
            trainingJohnCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                AttributeId = trainingMentorAttr.Id, Attribute = trainingMentorAttr,
                ReferenceValueId = trainingBobCso.Id, ReferenceValue = trainingBobCso, ConnectedSystemObject = trainingJohnCso
            });
        }

        var targetSystem = await CreateConnectedSystemAsync("AD Target");
        var targetExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var targetDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var targetManagerAttr = new ConnectedSystemObjectTypeAttribute { Name = "Manager", Type = AttributeDataType.Reference, Selected = true };
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "TargetUser",
            new List<ConnectedSystemObjectTypeAttribute> { targetExternalIdAttr, targetDisplayNameAttr, targetManagerAttr });

        var exportRule = new SyncRule
        {
            ConnectedSystemId = targetSystem.Id,
            Name = "AD Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = targetType.Id,
            ConnectedSystemObjectType = targetType,
            MetaverseObjectTypeId = mvType.Id,
            MetaverseObjectType = mvType,
            ProvisionToConnectedSystem = true
        };
        foreach (var (target, source) in new[] { (targetDisplayNameAttr, mvDisplayNameAttr), (targetManagerAttr, mvManagerAttr) })
        {
            exportRule.AttributeFlowRules.Add(new SyncRuleMapping
            {
                SyncRule = exportRule,
                TargetConnectedSystemAttribute = target,
                TargetConnectedSystemAttributeId = target.Id,
                Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
            });
        }
        DbContext.SyncRules.Add(exportRule);
        await DbContext.SaveChangesAsync();
        SyncRepo.SeedSyncRule(exportRule);

        return new ManagerReferenceTopology(hrSystem, trainingSystem, targetSystem);
    }

    private async Task<List<PreviewDelta>> PreviewAsync(ConnectedSystem connectedSystem)
    {
        var deltas = new List<PreviewDelta>();
        await foreach (var delta in Jim.ConnectedSystems.PreviewSynchronisedDeprovisioningAsync(connectedSystem.Id))
            deltas.Add(delta);
        return deltas;
    }

    private static List<PreviewDelta> Of(IEnumerable<PreviewDelta> deltas, ActivityRunProfileExecutionItemSyncOutcomeType transition) =>
        deltas.Where(d => d.TransitionType == transition).ToList();

    /// <summary>
    /// A Metaverse Object holding a Description contributed by HR's rule but no HR object: the state an earlier
    /// Connector Space clear leaves, which only the residue pass can reach.
    /// </summary>
    private MetaverseObject SeedStrandedMetaverseObject(DeprovisioningContext ctx)
    {
        var mvType = SyncRepo.MetaverseObjects.Values.First().Type!;
        var descriptionAttribute = mvType.Attributes.First(a => a.Id == ctx.MvDescriptionAttributeId);
        var strandedMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow };
        strandedMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = strandedMvo,
            Attribute = descriptionAttribute,
            AttributeId = descriptionAttribute.Id,
            StringValue = "Stranded value",
            ContributedBySyncRuleId = ctx.HrImportRule.Id,
            ContributedBySystemId = ctx.Hr.Id
        });
        SyncRepo.SeedMetaverseObject(strandedMvo);
        return strandedMvo;
    }

    /// <summary>
    /// Everything a deprovisioning run would change, flattened to comparable text: each Metaverse Object's values and
    /// deletion marks, each Connected System Object's status and join, each Pending Export, and the system's status.
    /// </summary>
    private string Snapshot()
    {
        var metaverseObjects = SyncRepo.MetaverseObjects.Values
            .OrderBy(m => m.Id)
            .Select(m => $"{m.Id}|{m.LastConnectorDisconnectedDate}|{m.DeletionEligibleDate}|" + string.Join(";", m.AttributeValues
                .OrderBy(v => v.AttributeId).ThenBy(v => v.Id)
                .Select(v => $"{v.AttributeId}={v.StringValue}@{v.ContributedBySystemId}/{v.ContributedBySyncRuleId}")));
        var connectedSystemObjects = SyncRepo.ConnectedSystemObjects.Values
            .OrderBy(c => c.Id)
            .Select(c => $"{c.Id}|{c.Status}|{c.MetaverseObjectId}|{c.JoinType}");
        var pendingExports = SyncRepo.PendingExports.Values
            .OrderBy(pe => pe.Id)
            .Select(pe => $"{pe.Id}|{pe.ChangeType}|{pe.ConnectedSystemObjectId}|{pe.AttributeValueChanges.Count}");
        var systems = DbContext.ConnectedSystems.OrderBy(s => s.Id).Select(s => $"{s.Id}|{s.Status}").ToList();
        return string.Join("\n", metaverseObjects.Concat(connectedSystemObjects).Concat(pendingExports).Concat(systems));
    }
}
