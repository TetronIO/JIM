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
/// Workflow tests for the mapping-deletion recall-or-keep choice (#1537, Phase 3). Deleting an Attribute Flow
/// mapping that contributed Metaverse attribute values offers recall (the default: the shipped #1533/#1536
/// orphan recall withdraws the values at the next Full Synchronisation of the contributing system) or keep
/// (the values' provenance is severed BEFORE the mapping row is deleted, permanently exempting them from the
/// orphan recall, and the choice is recorded on the deletion Activity). The same choice carries through the
/// portal's staged-removal path, where the Attribute Flow editor removes mappings in memory and the whole-rule
/// save persists the removals via the <see cref="SyncRuleMappingRemovalChoice"/> carrier.
/// </summary>
[TestFixture]
public class MappingDeletionChoiceWorkflowTests : WorkflowTestBase
{
    private const string HrDescription = "HR Description";
    private const string SharedEmployeeId = "EMP001";

    // -----------------------------------------------------------------------------------------------------------------
    // Direct mapping deletion (DeleteSyncRuleMappingAsync)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task DeleteSyncRuleMappingAsync_KeepChosenWithContributedValues_SeversProvenanceDeletesMappingAndRecordsChoiceAsync()
    {
        var ctx = await SetUpSoleContributorAsync();
        await RunFullSyncAsync(ctx.Hr);
        MirrorMetaverseValuesIntoDbContext();

        var mapping = ctx.HrImportRule.AttributeFlowRules.Single(m => m.TargetMetaverseAttributeId == ctx.MvDescriptionAttributeId);
        var mappingId = mapping.Id;

        var result = await Jim.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping, ctx.User, keepContributedValues: true);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var keptValue = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        var deletionActivity = DbContext.Activities.Single(a =>
            a.TargetType == ActivityTargetType.SynchronisationRule &&
            a.TargetOperationType == ActivityTargetOperationType.Delete);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(keptValue, Is.Not.Null, "the kept Description value must remain in place");
            Assert.That(keptValue!.ContributedBySyncRuleId, Is.Null,
                "keep must sever the Synchronisation Rule provenance so nothing ever recalls the value");
            Assert.That(keptValue!.ContributedBySystemId, Is.EqualTo(ctx.Hr.Id),
                "the denormalised Connected System provenance must be retained, mirroring what rule deletion's ON DELETE SET NULL produces");
            Assert.That(DbContext.SyncRuleMappings.SingleOrDefault(m => m.Id == mappingId), Is.Null,
                "the mapping row must be deleted");
            Assert.That(deletionActivity.Message, Does.Contain("kept"),
                "the deletion Activity must record that keep was chosen so the choice is auditable");
            Assert.That(result.ContributedValuesKept, Is.True);
            Assert.That(result.AffectedValueCount, Is.EqualTo(1), "the sole contributed Description value");
            Assert.That(result.AffectedObjectCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_KeepChosenWithNoContributedValues_DeletesWithoutKeepMessageAsync()
    {
        // A keep chosen when nothing was contributed has nothing to sever and nothing worth auditing:
        // the deletion proceeds exactly as the default path does.
        var ctx = await SetUpSoleContributorAsync();
        // No synchronisation has run, so no values exist to keep.

        var mapping = ctx.HrImportRule.AttributeFlowRules.Single(m => m.TargetMetaverseAttributeId == ctx.MvDescriptionAttributeId);
        var mappingId = mapping.Id;

        var result = await Jim.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping, ctx.User, keepContributedValues: true);

        var deletionActivity = DbContext.Activities.Single(a =>
            a.TargetType == ActivityTargetType.SynchronisationRule &&
            a.TargetOperationType == ActivityTargetOperationType.Delete);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DbContext.SyncRuleMappings.SingleOrDefault(m => m.Id == mappingId), Is.Null,
                "the mapping row must be deleted");
            Assert.That(deletionActivity.Message, Is.Null,
                "no values were present, so there is no keep choice to record");
            Assert.That(result.ContributedValuesKept, Is.False);
            Assert.That(result.AffectedValueCount, Is.Zero);
        }
    }

    [Test]
    public async Task FullSync_MappingDeletedWithKeep_ValuesAreNotRecalledAsync()
    {
        // The critical end-to-end exemption proof: keep severs the values' provenance before the mapping row is
        // deleted, and null-provenance values are never recalled, so the next Full Synchronisation of the
        // contributing system leaves them in place permanently.
        var ctx = await SetUpSoleContributorAsync();
        await RunFullSyncAsync(ctx.Hr);
        MirrorMetaverseValuesIntoDbContext();

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(HrDescription),
            "precondition: Description flowed while the mapping existed");

        await DeleteMappingViaServerAsync(ctx, keepContributedValues: true);

        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);
        Assert.That(activity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "the run must complete without unhandled errors");

        mvo = SyncRepo.MetaverseObjects.Values.Single();
        var keptValue = GetAttributeValue(mvo, ctx.MvDescriptionAttributeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(keptValue, Is.Not.Null,
                "the kept Description value must NOT be recalled by the contributing system's Full Synchronisation");
            Assert.That(keptValue!.StringValue, Is.EqualTo(HrDescription));
            Assert.That(keptValue!.ContributedBySyncRuleId, Is.Null, "the kept value carries no Synchronisation Rule provenance");
            Assert.That(GetAttributeValue(mvo, ctx.MvDisplayNameAttributeId), Is.Not.Null,
                "the surviving DisplayName mapping's value must be untouched (control)");
        }
    }

    [Test]
    public async Task FullSync_MappingDeletedWithRecallDefault_ValuesAreRecalledAsync()
    {
        // Regression pin for the default path through the real server delete: without keep, the shipped #1536
        // orphan recall withdraws the value at the next Full Synchronisation of the contributing system. The
        // recall mechanics themselves are covered in depth by RemovedMappingRecallWorkflowTests; this proves
        // the server's delete-with-choice entry point leaves them unchanged, and makes the keep test above
        // meaningful (the same mechanics minus keep must recall).
        var ctx = await SetUpSoleContributorAsync();
        await RunFullSyncAsync(ctx.Hr);
        MirrorMetaverseValuesIntoDbContext();

        await DeleteMappingViaServerAsync(ctx, keepContributedValues: false);

        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);
        Assert.That(activity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "the recall must complete without unhandled errors");

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId), Is.Null,
                "the default (recall) must withdraw the deleted mapping's value at the next Full Synchronisation");
            Assert.That(GetAttributeValue(mvo, ctx.MvDisplayNameAttributeId), Is.Not.Null,
                "the surviving DisplayName mapping's value must be untouched (control)");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Staged removal via the whole-rule save (CreateOrUpdateSyncRuleAsync + SyncRuleMappingRemovalChoice)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_StagedRemovalWithKeep_SeversProvenanceBeforeRowDeletionAsync()
    {
        var ctx = await SetUpStagedRemovalTopologyAsync();

        ctx.Rule.AttributeFlowRules.Remove(ctx.DescriptionMapping);
        var success = await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.Rule, ctx.User,
            mappingRemovalChoices: [new SyncRuleMappingRemovalChoice { MappingId = ctx.DescriptionMapping.Id, KeepContributedValues = true }]);

        var descriptionValues = DbContext.MetaverseObjectAttributeValues
            .Where(av => av.AttributeId == ctx.DescriptionAttributeId).ToList();
        var controlValues = DbContext.MetaverseObjectAttributeValues
            .Where(av => av.AttributeId == ctx.DisplayNameAttributeId).ToList();
        var updateActivity = DbContext.Activities.Single(a =>
            a.TargetType == ActivityTargetType.SynchronisationRule &&
            a.TargetOperationType == ActivityTargetOperationType.Update);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.True);
            Assert.That(descriptionValues, Is.Not.Empty);
            Assert.That(descriptionValues.Select(v => v.ContributedBySyncRuleId), Has.All.Null,
                "keep must sever the removed mapping's values before the row deletion within the save");
            Assert.That(descriptionValues.Select(v => v.ContributedBySystemId), Has.All.EqualTo(ctx.SystemId),
                "the denormalised Connected System provenance must be retained");
            Assert.That(controlValues.Select(v => v.ContributedBySyncRuleId), Has.All.EqualTo(ctx.Rule.Id),
                "the surviving DisplayName mapping's values must keep their provenance (control)");
            Assert.That(DbContext.SyncRuleMappings.SingleOrDefault(m => m.Id == ctx.DescriptionMapping.Id), Is.Null,
                "the staged removal must delete the mapping row");
            Assert.That(updateActivity.Message, Does.Contain("kept"),
                "the save's Activity must record that keep was chosen so the choice is auditable");
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_StagedRemovalWithoutKeep_LeavesProvenanceIntactAsync()
    {
        // The default (recall) staged removal severs nothing: the values keep their provenance, and the shipped
        // #1536 orphan recall withdraws them at the next Full Synchronisation of the contributing system.
        var ctx = await SetUpStagedRemovalTopologyAsync();

        ctx.Rule.AttributeFlowRules.Remove(ctx.DescriptionMapping);
        var success = await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.Rule, ctx.User,
            mappingRemovalChoices: [new SyncRuleMappingRemovalChoice { MappingId = ctx.DescriptionMapping.Id, KeepContributedValues = false }]);

        var descriptionValues = DbContext.MetaverseObjectAttributeValues
            .Where(av => av.AttributeId == ctx.DescriptionAttributeId).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.True);
            Assert.That(descriptionValues.Select(v => v.ContributedBySyncRuleId), Has.All.EqualTo(ctx.Rule.Id),
                "without keep, provenance must stay intact so the orphan recall can withdraw the values");
            Assert.That(DbContext.SyncRuleMappings.SingleOrDefault(m => m.Id == ctx.DescriptionMapping.Id), Is.Null,
                "the staged removal must delete the mapping row");
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_RemovalChoiceForMappingStillPresent_ThrowsAsync()
    {
        // A choice claiming a removal the save does not perform is a caller defect: honouring the keep would
        // sever live values out from under a mapping that still exists. Fast, hard failure over silent damage.
        var ctx = await SetUpStagedRemovalTopologyAsync();

        Assert.That(async () => await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.Rule, ctx.User,
                mappingRemovalChoices: [new SyncRuleMappingRemovalChoice { MappingId = ctx.DescriptionMapping.Id, KeepContributedValues = true }]),
            Throws.ArgumentException,
            "a removal choice naming a mapping still present on the rule must be refused");
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Topology builders and helpers
    // -----------------------------------------------------------------------------------------------------------------

    private sealed record SoleContributorContext(
        ConnectedSystem Hr,
        SyncRule HrImportRule,
        MetaverseObject User,
        int MvDescriptionAttributeId,
        int MvDisplayNameAttributeId);

    private sealed record StagedRemovalContext(
        SyncRule Rule,
        MetaverseObject User,
        SyncRuleMapping DescriptionMapping,
        int DescriptionAttributeId,
        int DisplayNameAttributeId,
        int SystemId);

    private static MetaverseObjectAttributeValue? GetAttributeValue(MetaverseObject mvo, int attributeId) =>
        mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == attributeId && !av.NullValue);

    /// <summary>
    /// Deletes the Description mapping through the real server path (as Remove-JIMSyncRuleMapping / the REST
    /// delete would), then makes sure the shared in-memory rule the sync processors read no longer holds the
    /// mapping. The row deletion detaches the mapping from the change tracker; whether EF's fix-up also removes
    /// it from the rule's collection navigation is an implementation detail, so the removal is made explicit,
    /// mirroring what a portal reload of the rule would show.
    /// </summary>
    private async Task DeleteMappingViaServerAsync(SoleContributorContext ctx, bool keepContributedValues)
    {
        var mapping = ctx.HrImportRule.AttributeFlowRules.Single(m => m.TargetMetaverseAttributeId == ctx.MvDescriptionAttributeId);
        await Jim.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping, ctx.User, keepContributedValues: keepContributedValues);
        ctx.HrImportRule.AttributeFlowRules.Remove(mapping);
    }

    /// <summary>
    /// The workflow harness holds two stores: the sync processors read and write Metaverse Objects through the
    /// in-memory SyncRepo, while the impact summary and the severing go through the Metaverse repository over
    /// the DbContext (in production both are one PostgreSQL database). Mirroring the sync-produced attribute
    /// values into the DbContext BY REFERENCE makes the two coherent: the severing's tracked mutation nulls
    /// <c>ContributedBySyncRuleId</c> on the very instances the next sync run evaluates.
    /// </summary>
    private void MirrorMetaverseValuesIntoDbContext()
    {
        // Detach processor-modified entities first (the WorkflowTestBase pattern), or this save would try to
        // persist mutations the sync made to shared instances whose rows were never written to the DbContext.
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Modified).ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        // Entry().State rather than DbSet.Add: Add walks the whole navigation graph and marks every untracked
        // entity it reaches for insertion, and a sync-produced Metaverse Object's graph reaches duplicate
        // instances of already-persisted rows (see "DbSet.Add Walks the Graph" in src/CLAUDE.md).
        foreach (var mvo in SyncRepo.MetaverseObjects.Values
                     .Where(mvo => !DbContext.MetaverseObjects.Local.Any(existing => existing.Id == mvo.Id)))
        {
            DbContext.Entry(mvo).State = Microsoft.EntityFrameworkCore.EntityState.Added;
            foreach (var attributeValue in mvo.AttributeValues)
                DbContext.Entry(attributeValue).State = Microsoft.EntityFrameworkCore.EntityState.Added;
        }
        DbContext.SaveChanges();
    }

    /// <summary>
    /// Builds the sole-contributor topology used by the direct-delete tests: HR projects and flows DisplayName,
    /// EmployeeId and Description from its own attributes; no other system contributes Description. Mirrors the
    /// RemovedMappingRecallWorkflowTests harness, plus the initiating user the audited server paths need.
    /// </summary>
    private async Task<SoleContributorContext> SetUpSoleContributorAsync()
    {
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var hrExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var hrDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var hrEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var hrDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "HrDescription", Type = AttributeDataType.Text, Selected = true };
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser",
            new List<ConnectedSystemObjectTypeAttribute> { hrExternalIdAttr, hrDisplayNameAttr, hrEmployeeIdAttr, hrDescriptionAttr });

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDescriptionAttr = new MetaverseAttribute
        {
            Name = "Description",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvDescriptionAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvDescriptionAttr);

        var hrImportRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDisplayNameAttr, hrDisplayNameAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvEmployeeIdAttr, hrEmployeeIdAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDescriptionAttr, hrDescriptionAttr, priority: 1));
        await DbContext.SaveChangesAsync();

        var user = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow, CachedDisplayName = "Test Administrator" };
        DbContext.MetaverseObjects.Add(user);
        await DbContext.SaveChangesAsync();

        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", SharedEmployeeId);
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrDescriptionAttr.Id, Attribute = hrDescriptionAttr, StringValue = HrDescription, ConnectedSystemObject = hrCso
        });

        return new SoleContributorContext(hrSystem, hrImportRule, user, mvDescriptionAttr.Id, mvDisplayNameAttr.Id);
    }

    /// <summary>
    /// Builds the staged-removal topology for the whole-rule save tests: an import rule with DisplayName and
    /// Description mappings, and contributed values seeded straight into the store the impact summary and the
    /// severing read (as SyncRuleDeletionRecallWorkflowTests does for the rule-level choice).
    /// </summary>
    private async Task<StagedRemovalContext> SetUpStagedRemovalTopologyAsync()
    {
        var system = await CreateConnectedSystemAsync("HR Source");
        var externalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var descriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "HrDescription", Type = AttributeDataType.Text, Selected = true };
        var csoType = await CreateCsoTypeAsync(system.Id, "HrUser",
            new List<ConnectedSystemObjectTypeAttribute> { externalIdAttr, displayNameAttr, descriptionAttr });

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvDescriptionAttr = new MetaverseAttribute
        {
            Name = "Description",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvDescriptionAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvDescriptionAttr);

        var rule = await CreateImportSyncRuleAsync(system.Id, csoType, mvType, "HR Import");
        var descriptionMapping = BuildDirectImportMapping(rule, mvDescriptionAttr, descriptionAttr, priority: 1);
        rule.AttributeFlowRules.Add(BuildDirectImportMapping(rule, mvDisplayNameAttr, displayNameAttr));
        rule.AttributeFlowRules.Add(descriptionMapping);
        await DbContext.SaveChangesAsync();

        var user = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow, CachedDisplayName = "Test Administrator" };
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow };
        DbContext.MetaverseObjects.AddRange(user, mvo);
        await DbContext.SaveChangesAsync();

        DbContext.MetaverseObjectAttributeValues.AddRange(
            new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                MetaverseObject = mvo,
                Attribute = mvDescriptionAttr,
                AttributeId = mvDescriptionAttr.Id,
                StringValue = HrDescription,
                ContributedBySyncRuleId = rule.Id,
                ContributedBySystemId = system.Id
            },
            new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                MetaverseObject = mvo,
                Attribute = mvDisplayNameAttr,
                AttributeId = mvDisplayNameAttr.Id,
                StringValue = "John Smith",
                ContributedBySyncRuleId = rule.Id,
                ContributedBySystemId = system.Id
            });
        await DbContext.SaveChangesAsync();

        return new StagedRemovalContext(rule, user, descriptionMapping, mvDescriptionAttr.Id, mvDisplayNameAttr.Id, system.Id);
    }

    private static SyncRuleMapping BuildDirectImportMapping(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source, int priority = int.MaxValue)
    {
        return new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            Priority = priority,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        };
    }

    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        await RunFullSyncReturningActivityAsync(connectedSystem);
    }

    private async Task<Activity> RunFullSyncReturningActivityAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
