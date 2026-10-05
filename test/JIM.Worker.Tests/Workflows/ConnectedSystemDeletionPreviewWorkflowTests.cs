// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Preview;
using JIM.Models.Staging;
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

        Assert.That(Of(deltas, ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject).Select(d => d.ConnectedSystemObjectId),
            Is.EqualTo(new Guid?[] { targetCso.Id }));
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
    // Helpers
    // -----------------------------------------------------------------------------------------------------------------

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
