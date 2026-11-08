// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
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
/// A synchronisation writes a joined Metaverse Object only when it changed something about it (#2831). Every joined
/// object used to be queued for the page's batch update, whose reconcile re-reads every value the object holds (every
/// member of a large group), even when the run changed nothing. What makes an object worth writing: a value added or
/// removed, a join (which can cancel a scheduled deletion), or a value whose provenance this run took over without
/// replacing it (#1292), in the scalar pass or the deferred reference pass.
/// <para>
/// The in-memory repository stores the instances the run holds, so its state cannot tell a written object from an
/// unwritten one; the spy records which objects reached the batch update. The real write path is covered against
/// PostgreSQL by <c>UnchangedMetaverseObjectWriteDatabaseTests</c>.
/// </para>
/// </summary>
[TestFixture]
public class UnchangedMetaverseObjectWriteWorkflowTests : WorkflowTestBase
{
    private SpySyncRepository _spySyncRepo = null!;

    [SetUp]
    public void SetUpSpySyncRepo()
    {
        _spySyncRepo = new SpySyncRepository();
        _spySyncRepo.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        SyncRepo = _spySyncRepo;
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);
    }

    [Test]
    public async Task FullSync_OneOfTwoJoinedObjectsChanged_WritesOnlyTheChangedMetaverseObjectAsync()
    {
        var (system, rule, mvType) = await ArrangeHrSystemAsync();
        var displayNameId = mvType.Attributes.First(a => a.Name == "DisplayName").Id;
        var unchangedCso = await CreateCsoAsync(system.Id, rule.ConnectedSystemObjectType!, "Alice Unchanged", "EMP001");
        var changedCso = await CreateCsoAsync(system.Id, rule.ConnectedSystemObjectType!, "Bob Before", "EMP002");
        await RunFullSyncAsync(system);
        var unchangedMvoId = (await ReloadEntityAsync(unchangedCso)).MetaverseObjectId!.Value;
        var changedMvoId = (await ReloadEntityAsync(changedCso)).MetaverseObjectId!.Value;
        _spySyncRepo.BatchMvoUpdateIds.Clear();

        changedCso.AttributeValues.Single(av => av.Attribute.Name == "DisplayName").StringValue = "Bob After";
        var activity = await RunFullSyncAsync(system);

        var unchangedMvo = SyncRepo.MetaverseObjects[unchangedMvoId];
        var changedMvo = SyncRepo.MetaverseObjects[changedMvoId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_spySyncRepo.BatchMvoUpdateIds, Is.EqualTo(new[] { changedMvoId }),
                "only the Metaverse Object whose value changed reaches the batch update, and so the value read it makes");
            Assert.That(changedMvo.AttributeValues.Single(av => av.AttributeId == displayNameId).StringValue, Is.EqualTo("Bob After"),
                "the changed object's new value is written");
            Assert.That(unchangedMvo.AttributeValues.Single(av => av.AttributeId == displayNameId).StringValue, Is.EqualTo("Alice Unchanged"),
                "the unchanged object keeps its value");
            Assert.That(activity.RunProfileExecutionItems.Select(i => (i.ConnectedSystemObjectId, i.ObjectChangeType)),
                Is.EqualTo(new[] { ((Guid?)changedCso.Id, ObjectChangeType.AttributeFlow) }),
                "the execution items are what they were: one Attribute Flow for the changed object, none for the unchanged one");
            Assert.That(SyncRepo.MetaverseObjectChanges.Values.Count(c => c.MetaverseObjectId == unchangedMvoId), Is.EqualTo(1),
                "the unchanged object has only its projection's change record");
            Assert.That(SyncRepo.MetaverseObjectChanges.Values.Count(c => c.MetaverseObjectId == changedMvoId), Is.EqualTo(2),
                "the changed object gains one change record for the Attribute Flow");
        }
    }

    [Test]
    public async Task FullSync_NothingChanged_WritesNoMetaverseObjectAsync()
    {
        var (system, rule, _) = await ArrangeHrSystemAsync();
        await CreateCsoAsync(system.Id, rule.ConnectedSystemObjectType!, "Alice", "EMP001");
        await CreateCsoAsync(system.Id, rule.ConnectedSystemObjectType!, "Bob", "EMP002");
        await RunFullSyncAsync(system);
        _spySyncRepo.BatchMvoUpdateIds.Clear();

        var activity = await RunFullSyncAsync(system);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_spySyncRepo.BatchMvoUpdateIds, Is.Empty, "a run that changes nothing writes no Metaverse Object");
            Assert.That(activity.RunProfileExecutionItems, Is.Empty, "and records nothing, as before");
        }
    }

    /// <summary>
    /// A contribution that supplies the value the object already holds stages no addition or removal, but takes over
    /// the value's provenance when the stamp is stale (#1292), here the null a deleted rule's <c>ON DELETE SET NULL</c>
    /// leaves. The provenance is only stored if the object is written, so it still must be.
    /// </summary>
    [Test]
    public async Task FullSync_ValueUnchangedButProvenanceTakenOver_StillWritesTheMetaverseObjectAsync()
    {
        var (system, rule, mvType) = await ArrangeHrSystemAsync();
        var staleCso = await CreateCsoAsync(system.Id, rule.ConnectedSystemObjectType!, "Alice", "EMP001");
        await CreateCsoAsync(system.Id, rule.ConnectedSystemObjectType!, "Bob", "EMP002");
        await RunFullSyncAsync(system);
        var staleMvoId = (await ReloadEntityAsync(staleCso)).MetaverseObjectId!.Value;
        var displayNameId = mvType.Attributes.First(a => a.Name == "DisplayName").Id;
        var staleValue = SyncRepo.MetaverseObjects[staleMvoId].AttributeValues.Single(av => av.AttributeId == displayNameId);
        staleValue.ContributedBySyncRuleId = null;
        staleValue.ContributedBySystemId = null;
        _spySyncRepo.BatchMvoUpdateIds.Clear();

        await RunFullSyncAsync(system);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_spySyncRepo.BatchMvoUpdateIds, Is.EqualTo(new[] { staleMvoId }),
                "the object whose provenance was taken over is written; the untouched one is not");
            Assert.That(staleValue.ContributedBySyncRuleId, Is.EqualTo(rule.Id), "the live rule takes the value over");
            Assert.That(staleValue.ContributedBySystemId, Is.EqualTo(system.Id));
        }
    }

    /// <summary>
    /// As above, for a reference value. References flow in the deferred pass, after the scalar pass has already decided
    /// whether to write the object, so the deferred pass must queue an object whose only change is a taken-over stamp.
    /// </summary>
    [Test]
    public async Task FullSync_ReferenceUnchangedButProvenanceTakenOver_StillWritesTheMetaverseObjectAsync()
    {
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var externalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var managerAttr = new ConnectedSystemObjectTypeAttribute { Name = "Manager", Type = AttributeDataType.Reference, Selected = true };
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser", [externalIdAttr, displayNameAttr, managerAttr]);

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvManagerAttr = new MetaverseAttribute
        {
            Name = "Manager", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvType], PredefinedSearchAttributes = []
        };
        DbContext.MetaverseAttributes.Add(mvManagerAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvManagerAttr);

        var importRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        importRule.AttributeFlowRules.Add(DirectMapping(importRule, mvType.Attributes.First(a => a.Name == "DisplayName"), displayNameAttr));
        importRule.AttributeFlowRules.Add(DirectMapping(importRule, mvManagerAttr, managerAttr));
        await DbContext.SaveChangesAsync();

        var maryCso = await CreateCsoAsync(hrSystem.Id, hrType, "Mary Manager");
        var johnCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith");
        johnCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = managerAttr.Id, Attribute = managerAttr, ReferenceValueId = maryCso.Id, ReferenceValue = maryCso,
            ConnectedSystemObject = johnCso
        });
        await RunFullSyncAsync(hrSystem);

        var johnMvoId = (await ReloadEntityAsync(johnCso)).MetaverseObjectId!.Value;
        var managerValue = SyncRepo.MetaverseObjects[johnMvoId].AttributeValues.Single(av => av.AttributeId == mvManagerAttr.Id);
        managerValue.ContributedBySyncRuleId = null;
        managerValue.ContributedBySystemId = null;
        _spySyncRepo.BatchMvoUpdateIds.Clear();

        await RunFullSyncAsync(hrSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_spySyncRepo.BatchMvoUpdateIds, Is.EqualTo(new[] { johnMvoId }),
                "the object whose reference provenance was taken over is written; the manager's object is not");
            Assert.That(managerValue.ContributedBySyncRuleId, Is.EqualTo(importRule.Id), "the live rule takes the reference over");
        }
    }

    /// <summary>
    /// A join that brings no value the object does not already hold stages nothing, but when it cancels a scheduled
    /// deletion it clears the deletion markers on the row, and only the object's write stores that.
    /// </summary>
    [Test]
    public async Task FullSync_RejoinWithIdenticalValuesCancelsScheduledDeletion_StillWritesTheMetaverseObjectAsync()
    {
        var (system, rule, mvType) = await ArrangeHrSystemAsync();
        var csoType = rule.ConnectedSystemObjectType!;
        csoType.RemoveContributedAttributesOnObsoletion = false;
        mvType.DeletionGracePeriod = TimeSpan.FromDays(30);
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var csoEmployeeId = csoType.Attributes.First(a => a.Name == "EmployeeId");
        rule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = rule, SyncRuleId = rule.Id, Order = 0, CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeId, TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = csoEmployeeId, ConnectedSystemAttributeId = csoEmployeeId.Id } }
        });
        await DbContext.SaveChangesAsync();

        var leaver = await CreateCsoAsync(system.Id, csoType, "John Smith", "E100");
        await RunFullSyncAsync(system);
        var mvoId = (await ReloadEntityAsync(leaver)).MetaverseObjectId!.Value;
        leaver.Status = ConnectedSystemObjectStatus.Obsolete;
        leaver.LastUpdated = DateTime.UtcNow;
        await RunFullSyncAsync(system);
        var mvo = SyncRepo.MetaverseObjects[mvoId];
        Assert.That(mvo.LastConnectorDisconnectedDate, Is.Not.Null, "precondition: the leaver's departure scheduled the deletion");
        var valuesBeforeRejoin = mvo.AttributeValues.Select(av => av.StringValue).Order().ToList();
        _spySyncRepo.BatchMvoUpdateIds.Clear();

        var rejoiner = await CreateCsoAsync(system.Id, csoType, "John Smith", "E100");
        await RunFullSyncAsync(system);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.ConnectedSystemObjects[rejoiner.Id].MetaverseObjectId, Is.EqualTo(mvoId), "precondition: the rejoiner joined");
            Assert.That(mvo.AttributeValues.Select(av => av.StringValue).Order(), Is.EqualTo(valuesBeforeRejoin),
                "precondition: the join brought no value the object did not already hold");
            Assert.That(mvo.LastConnectorDisconnectedDate, Is.Null, "the join cancels the scheduled deletion");
            Assert.That(_spySyncRepo.BatchMvoUpdateIds, Is.EqualTo(new[] { mvoId }), "and the object is written so the cancellation is stored");
        }
    }

    #region Harness

    /// <summary>An HR system whose import rule projects each object and flows its display name and employee id.</summary>
    private async Task<(ConnectedSystem System, SyncRule Rule, MetaverseObjectType MvType)> ArrangeHrSystemAsync()
    {
        var system = await CreateConnectedSystemAsync("HR Source");
        var csoType = await CreateCsoTypeAsync(system.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        var rule = await CreateImportSyncRuleAsync(system.Id, csoType, mvType, "HR Import");
        rule.AttributeFlowRules.Add(DirectMapping(rule, mvType.Attributes.First(a => a.Name == "DisplayName"),
            csoType.Attributes.First(a => a.Name == "DisplayName")));
        rule.AttributeFlowRules.Add(DirectMapping(rule, mvType.Attributes.First(a => a.Name == "EmployeeId"),
            csoType.Attributes.First(a => a.Name == "EmployeeId")));
        await DbContext.SaveChangesAsync();
        return (system, rule, mvType);
    }

    private static SyncRuleMapping DirectMapping(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source) => new()
    {
        SyncRule = rule,
        SyncRuleId = rule.Id,
        TargetMetaverseAttribute = target,
        TargetMetaverseAttributeId = target.Id,
        Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
    };

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem system)
    {
        var profile = await CreateRunProfileAsync(system.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(system.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, system, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    /// <summary>Records which Metaverse Objects reach the page flush's batch update.</summary>
    private sealed class SpySyncRepository : JIM.InMemoryData.SyncRepository
    {
        public List<Guid> BatchMvoUpdateIds { get; } = [];

        public override Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
        {
            var list = metaverseObjects.ToList();
            BatchMvoUpdateIds.AddRange(list.Select(mvo => mvo.Id));
            return base.UpdateMetaverseObjectsAsync(list);
        }
    }

    #endregion
}
