// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A Projected sync outcome's TargetEntityDescription must never be an all-zero GUID.
///
/// Outcome nodes are built before the Metaverse Object is persisted, so its id is still
/// <see cref="Guid.Empty"/> and its name has not flowed yet. MetaverseObject.NameOrId falls back to
/// the id, which at that moment stringifies to "00000000-0000-0000-0000-000000000000". The
/// retroactive pass that runs after persistence only fills a description that is blank, so an
/// all-zero GUID written at creation time survives it and reaches the causality view as the
/// Identity's name.
/// </summary>
[TestFixture]
public class ProjectedOutcomeDescriptionTests : WorkflowTestBase
{
    private const string EmptyGuidText = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    /// The projection path that flows attributes (outcome built in ProcessMetaverseObjectChangesAsync).
    /// </summary>
    [Test]
    public async Task ProcessMetaverseObjectChanges_CsoProjected_OutcomeDescriptionNamesTheIdentityAsync()
    {
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");

        // The shared harness names its Metaverse attribute "DisplayName", but ObjectNaming matches
        // Metaverse name attributes *exactly* against the curated built-in names ("Display Name"), so
        // flowing into the harness attribute would leave the Identity unnamed and the assertion below
        // would be testing the harness rather than JIM. Add a correctly-named attribute and flow into
        // that, so the outcome description exercises the real naming path.
        var mvDisplayNameAttr = new MetaverseAttribute
        {
            Name = Constants.BuiltInAttributes.DisplayName,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvType],
            PredefinedSearchAttributes = []
        };
        DbContext.MetaverseAttributes.Add(mvDisplayNameAttr);
        mvType.Attributes.Add(mvDisplayNameAttr);
        await DbContext.SaveChangesAsync();

        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var csoDisplayNameAttr = sourceType.Attributes.First(a => a.Name == "DisplayName");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = csoDisplayNameAttr,
                ConnectedSystemAttributeId = csoDisplayNameAttr.Id
            }}
        });

        await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        var outcomes = ProjectedAndFlowOutcomes(fullSyncActivity);
        Assert.That(outcomes, Is.Not.Empty, "Should have a Projected outcome to assert on");

        foreach (var outcome in outcomes)
        {
            Assert.That(outcome.TargetEntityDescription, Is.Not.EqualTo(EmptyGuidText),
                $"The {outcome.OutcomeType} outcome describes the Identity as an all-zero GUID. The " +
                "description was taken from NameOrId before the Metaverse Object was persisted, so it " +
                "fell back to an id that did not exist yet.");
            Assert.That(outcome.TargetEntityDescription, Is.EqualTo("John Smith"),
                $"The {outcome.OutcomeType} outcome should name the Identity once its attributes have flowed.");
        }
    }

    /// <summary>
    /// The projection path with no Attribute Flow (outcome built in ProcessActiveConnectedSystemObjectAsync
    /// from the change result). The Identity has no name to resolve here, so the description must fall back
    /// to the Metaverse Object's real id rather than to the empty one it had at creation time.
    /// </summary>
    [Test]
    public async Task ProcessActiveConnectedSystemObject_CsoProjectedWithoutFlow_OutcomeDescriptionUsesTheRealIdAsync()
    {
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");

        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "CSO should be joined to an MVO after Full Sync");

        var outcomes = ProjectedAndFlowOutcomes(fullSyncActivity);
        Assert.That(outcomes, Is.Not.Empty, "Should have a Projected outcome to assert on");

        foreach (var outcome in outcomes)
        {
            Assert.That(outcome.TargetEntityDescription, Is.Not.EqualTo(EmptyGuidText),
                $"The {outcome.OutcomeType} outcome describes the Identity as an all-zero GUID rather than " +
                "as the id it was actually assigned.");
        }
    }

    private static List<ActivityRunProfileExecutionItemSyncOutcome> ProjectedAndFlowOutcomes(Activity activity)
    {
        return activity.RunProfileExecutionItems
            .SelectMany(r => r.SyncOutcomes)
            .Where(o => o.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.Projected
                or ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow)
            .ToList();
    }
}
