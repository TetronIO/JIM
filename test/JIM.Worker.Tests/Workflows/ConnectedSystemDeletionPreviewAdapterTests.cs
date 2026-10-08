// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Preview;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.TestSupport;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// The Connected System deletion preview adapter (#134): the framework-facing face of the read-only deprovisioning
/// harness. The harness's classification and its agreement with the real run are proven in
/// <see cref="ConnectedSystemDeletionPreviewWorkflowTests"/>; these cover what the adapter adds on top: what it refuses
/// or warns about before evaluating anything, what it estimates, and how it counts.
/// </summary>
[TestFixture]
public class ConnectedSystemDeletionPreviewAdapterTests : SynchronisedDeprovisioningTestBase
{
    private ConnectedSystemDeletionPreviewAdapter Adapter => new(Jim);

    [Test]
    public void Adapter_ServesTheDeletionSurfaceWithAnEmptyProposal()
    {
        var adapter = Adapter;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(adapter.Surface, Is.EqualTo(ConfigurationChangePreviewSurface.ConnectedSystemDeletion));
            Assert.That(adapter.ProducesDeltas, Is.True);
            Assert.That(adapter.ProposalType, Is.EqualTo(typeof(ConnectedSystemDeletionProposal)));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ValidateAsync_SystemInService_FindsNothingAsync()
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);

        var findings = await Adapter.ValidateAsync(ContextFor(ctx.Hr.Id));

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public async Task ValidateAsync_SystemAlreadyBeingDeleted_BlocksAsync()
    {
        // Its deprovisioning has started, so part of what the preview would describe has already happened.
        var ctx = await SetUpSoleContributorWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        await FenceSystemAsync(ctx.Hr);

        var findings = await Adapter.ValidateAsync(ContextFor(ctx.Hr.Id));

        Assert.That(findings.Select(f => f.Severity), Is.EqualTo(new[] { PreviewValidationSeverity.Blocking }));
    }

    [Test]
    public async Task ValidateAsync_SystemDoesNotExist_BlocksAsync()
    {
        var findings = await Adapter.ValidateAsync(ContextFor(987_654));

        Assert.That(findings.Select(f => f.Severity), Is.EqualTo(new[] { PreviewValidationSeverity.Blocking }));
    }

    [Test]
    public async Task ValidateAsync_SurvivingDerivedFlowReadsARecalledAttribute_WarnsNamingTheAttributeAndItsHostAsync()
    {
        // Deprovisioning marks the objects whose derived values read a recalled attribute for re-derivation by their
        // host's next synchronisation; it does not re-derive them, so the preview cannot show what they become.
        await EnableAllFeatureFlagsAsync();
        var ctx = await SetUpTwoContributorsWithExportTargetAsync();
        await AddDerivedFlowAsync(ctx.TrainingImportRuleId, "mv[\"Description\"] + \"@corp.local\"");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);

        var findings = await Adapter.ValidateAsync(ContextFor(ctx.Hr.Id));

        Assert.That(findings, Has.Count.EqualTo(1));
        var finding = findings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding.Severity, Is.EqualTo(PreviewValidationSeverity.Warning));
            Assert.That(finding.MetaverseAttributeName, Is.EqualTo("Description"));
            Assert.That(finding.Message, Does.Contain("Training Source"));
        }
    }

    [Test]
    public async Task ValidateAsync_DerivedFlowHostedOnTheDeletedSystem_DoesNotWarnAsync()
    {
        // The deleted system's own derived flows go with it, exactly as execution leaves them out of the graph it
        // marks from; there is nothing left to re-derive them.
        await EnableAllFeatureFlagsAsync();
        var ctx = await SetUpTwoContributorsWithExportTargetAsync();
        await AddDerivedFlowAsync(ctx.HrImportRule.Id, "mv[\"Description\"] + \"@corp.local\"");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);

        var findings = await Adapter.ValidateAsync(ContextFor(ctx.Hr.Id));

        Assert.That(findings, Is.Empty);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Estimate and counts
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task EstimateCostAsync_TheSystemsObjectsByTheAttributesItContributesAsync()
    {
        var ctx = await SetUpTwoObjectsWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);

        var estimate = await Adapter.EstimateCostAsync(ContextFor(ctx.Hr.Id));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimate.AffectedObjects, Is.EqualTo(2), "every one of the system's objects is obsoleted");
            Assert.That(estimate.DeltasPerObject, Is.EqualTo(3), "HR contributes DisplayName, EmployeeId and Description");
        }
    }

    [Test]
    public async Task CountImpactAsync_CountsDistinctObjectsOncePerTransitionAsync()
    {
        // Two people lose three values each and their two target accounts are updated in two attributes each: six
        // clears and four export rows, but two objects either way, because the count is what an administrator
        // consents to and it reads "objects would ...".
        var ctx = await SetUpTwoObjectsWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        ApplyStagedExports(ctx.Target);

        var counts = await Adapter.CountImpactAsync(ContextFor(ctx.Hr.Id));

        Assert.That(counts.Select(c => (c.TransitionType, c.ObjectCount)), Is.EquivalentTo(new[]
        {
            (ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor, 2),
            (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport, 2)
        }));
    }

    [Test]
    public async Task CountImpactAsync_QueuedChangesWithdrawnInTwoTargets_CountsEachTargetAccountAsync()
    {
        // One person whose revised Description is queued for two target accounts, both already holding the value the
        // deletion hands Description to (#2011). A withdrawal happens to the account whose queue loses the change, as an
        // update does, so this is two objects, not one person.
        var ctx = await SetUpTwoContributorsWithExportTargetAsync();
        var mvType = ctx.HrImportRule.MetaverseObjectType!;
        var secondTarget = await AddExportTargetAsync(mvType,
            mvType.Attributes.First(a => a.Id == ctx.MvDisplayNameAttributeId),
            mvType.Attributes.First(a => a.Id == ctx.MvDescriptionAttributeId));
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);
        foreach (var targetCso in SyncRepo.ConnectedSystemObjects.Values
                     .Where(c => c.ConnectedSystemId == ctx.Target.Id || c.ConnectedSystemId == secondTarget.System.Id))
        {
            targetCso.Status = ConnectedSystemObjectStatus.Normal;
            foreach (var (name, value) in new[] { ("DisplayName", "John Smith"), ("Description", TrainingDescription) })
            {
                var attribute = targetCso.Type.Attributes.Single(a => a.Name == name);
                targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    AttributeId = attribute.Id, Attribute = attribute, StringValue = value, ConnectedSystemObject = targetCso
                });
            }
        }
        SyncRepo.ClearAllPendingExports();
        await QueueRevisedHrDescriptionAsync(ctx);

        var counts = await Adapter.CountImpactAsync(ContextFor(ctx.Hr.Id));

        Assert.That(counts.SingleOrDefault(c => c.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportChangesWithdrawn)?.ObjectCount,
            Is.EqualTo(2), string.Join("; ", counts.Select(c => $"{c.TransitionType}={c.ObjectCount}")));
    }

    [Test]
    public async Task CreateImpactCounterAsync_FedItsOwnDeltas_CountsAsCountImpactAsyncDoesAsync()
    {
        var ctx = await SetUpTwoObjectsWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        ApplyStagedExports(ctx.Target);

        await JIM.Worker.Tests.Servers.PreviewImpactCounterEquivalence.AssertCountsFromItsOwnDeltasAsync(Adapter, ContextFor(ctx.Hr.Id));
    }

    [Test]
    public async Task EvaluateDeltasAsync_StreamsTheDeprovisioningPreviewAsync()
    {
        var ctx = await SetUpTwoContributorsWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);

        var fromAdapter = new List<PreviewDelta>();
        await foreach (var delta in Adapter.EvaluateDeltasAsync(ContextFor(ctx.Hr.Id), CancellationToken.None))
            fromAdapter.Add(delta);
        var fromHarness = new List<PreviewDelta>();
        await foreach (var delta in Jim.ConnectedSystems.PreviewSynchronisedDeprovisioningAsync(ctx.Hr.Id))
            fromHarness.Add(delta);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromAdapter, Is.Not.Empty, "precondition: the scenario has consequences");
            Assert.That(fromAdapter, Is.EquivalentTo(fromHarness));
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_Cancelled_StopsAsync()
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.That(async () =>
        {
            await foreach (var _ in Adapter.EvaluateDeltasAsync(ContextFor(ctx.Hr.Id), cancellation.Token))
            {
            }
        }, Throws.InstanceOf<OperationCanceledException>());
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------------------------------

    private static PreviewContext ContextFor(int connectedSystemId) => new()
    {
        Surface = ConfigurationChangePreviewSurface.ConnectedSystemDeletion,
        ActivityId = Guid.NewGuid(),
        TargetId = connectedSystemId,
        ProposedConfiguration = new ConnectedSystemDeletionProposal()
    };

    /// <summary>
    /// Tests run as though the features had shipped (test/CLAUDE.md); the workflow harness reads Service Settings from
    /// its database, so the flag rows are stored there.
    /// </summary>
    private async Task EnableAllFeatureFlagsAsync()
    {
        DbContext.ServiceSettingItems.AddRange(await InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled().GetAllSettingsAsync());
        await DbContext.SaveChangesAsync();
    }

    /// <summary>
    /// A derived flow on <paramref name="syncRuleId"/>'s rule: an Email attribute computed from the Metaverse values
    /// <paramref name="expression"/> reads.
    /// </summary>
    private async Task AddDerivedFlowAsync(int syncRuleId, string expression)
    {
        var rule = SyncRepo.SyncRules.Values.Single(r => r.Id == syncRuleId);
        var mvType = rule.MetaverseObjectType!;
        var email = new MetaverseAttribute
        {
            Name = "Email",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(email);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(email);

        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = email,
            TargetMetaverseAttributeId = email.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = expression } }
        });
        await DbContext.SaveChangesAsync();
    }
}
