// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Saving an import Synchronisation Rule reaches the objects that already exist at the Connected System's next
/// synchronisation, Delta or Full, without a Full Synchronisation being run by hand (#2615). The save goes through the
/// application layer, the path the portal, REST API and PowerShell share, and records a change awaiting synchronisation;
/// an ordinary Delta Synchronisation then claims it, processes every object of the rule's Object Type, and applies it.
/// <para>
/// Topology: an "HR" system whose User type holds Alice and Bob (Employees) and Carol (a Contractor), all projected and
/// joined by a first Full Synchronisation. Nothing about any of them changes afterwards, so what a Delta processes is
/// down to the saved rule alone.
/// </para>
/// </summary>
[TestFixture]
public class ImportRuleSaveAppliedBySynchronisationWorkflowTests : WorkflowTestBase
{
    private ConnectedSystem _hr = null!;
    private SyncRule _rule = null!;
    private MetaverseObject _administrator = null!;
    private readonly HashSet<long> _mirroredIds = [];

    /// <summary>
    /// The topology, one Full Synchronisation to join everyone, and the rule saved once so that the change history holds
    /// a baseline: without one, a first save has nothing to be compared with and is recorded as awaiting synchronisation
    /// whatever it changed. That baseline save is applied by a Delta Synchronisation, so every test starts settled.
    /// </summary>
    [SetUp]
    public async Task SetUpAsync()
    {
        // One fixture instance serves every test, and each test's database numbers its rows from 1 again.
        _mirroredIds.Clear();

        _hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(_hr.Id, "User",
        [
            new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "Title", Type = AttributeDataType.Text, Selected = true }
        ]);
        var mvType = await CreateMvObjectTypeAsync("Person");

        var rule = await CreateImportSyncRuleAsync(_hr.Id, hrType, mvType, "HR Import");
        rule.AttributeFlowRules.Add(ImportFlow(rule, hrType, "EmployeeId", mvType.Attributes.First(a => a.Name == "EmployeeId")));
        rule.AttributeFlowRules.Add(ImportFlow(rule, hrType, "DisplayName", mvType.Attributes.First(a => a.Name == "DisplayName")));
        await DbContext.SaveChangesAsync();

        SeedCso(hrType, "E1", "Alice", "Employee");
        SeedCso(hrType, "E2", "Bob", "Employee");
        SeedCso(hrType, "E3", "Carol", "Contractor");

        await RunAsync(ConnectedSystemRunType.FullSynchronisation);
        Assert.That(SyncRepo.ConnectedSystemObjects.Values.Select(c => c.MetaverseObjectId), Has.All.Not.Null, "arrange: everyone is joined");

        _administrator = await NewAdministratorAsync();

        // The harness detaches what the synchronisation modified, the rule included, so the DbContext and the in-memory
        // synchronisation store would each hold their own copy. Load it back through the configuration side and hand that
        // tracked instance to the synchronisation store too: one rule, as in production.
        _rule = await Jim.ConnectedSystems.GetSyncRuleAsync(rule.Id) ?? throw new InvalidOperationException("arrange: the rule reloads");
        SyncRepo.SeedSyncRule(_rule);

        var baseline = await EditAndSaveRuleAsync(_ => { });
        Assert.That(DbContext.Activities.AsNoTracking().Where(a => a.SyncRuleId == _rule.Id).Select(a => a.ConfigurationChangeSnapshot),
            Has.Some.Not.Null, "arrange: the change history works here, so what later saves record is the capture's verdict and not its failure's");
        Assert.That(baseline, Has.Count.EqualTo(1), "arrange: the first save has no earlier version to be compared with, so it is recorded");
        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);
        Assert.That(baseline.Single().AppliedAt, Is.Not.Null, "arrange: the baseline save is applied");
    }

    [Test]
    public async Task DeltaSync_AfterAnImportRulesScopeIsNarrowed_AppliesTheNewScopeToObjectsThatHaveNotChangedAsync()
    {
        // The saved change: only Employees are in scope. Nothing about Carol has changed.
        var recorded = await EditAndSaveRuleAsync(rule => rule.ObjectScopingCriteriaGroups.Add(EmployeesOnly(rule)));

        Assert.That(recorded, Has.Count.EqualTo(1), "the save recorded one change awaiting synchronisation");
        var change = DbContext.ConfigurationChangeSynchronisations.AsNoTracking().OrderBy(c => c.Id).Last();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.ConnectedSystemObjectTypeId, Is.EqualTo(_rule.ConnectedSystemObjectTypeId), "for the Object Type the rule is on");
            Assert.That(change.SyncRuleId, Is.EqualTo(_rule.Id));
            Assert.That(change.ConfigurationChangeActivityId, Is.Not.Null, "naming the save that asked for it");
            Assert.That(change.Reason, Does.Contain("HR Import").And.Contain("Scope"));
        }

        var run = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        var carol = SyncRepo.ConnectedSystemObjects.Values.Single(c => DisplayNameOf(c) == "Carol");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.ProcessedCsoIds, Has.Count.EqualTo(3), "every object of the type was processed, though none had changed");
            Assert.That(carol.MetaverseObjectId, Is.Null, "Carol is out of the new scope and was disconnected");
            Assert.That(SyncRepo.ConnectedSystemObjects.Values.Where(c => c != carol).Select(c => c.MetaverseObjectId), Has.All.Not.Null,
                "Alice and Bob are still joined");
            Assert.That(recorded.Single().ClaimedByActivityId, Is.EqualTo(run.Activity.Id));
            Assert.That(recorded.Single().AppliedAt, Is.Not.Null, "the run applied the change");
        }

        var followUp = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);
        Assert.That(followUp.ProcessedCsoIds, Is.Empty, "once applied, the next Delta Synchronisation is back to changed objects only");
    }

    [Test]
    public async Task DeltaSync_AfterAnImportRuleIsRenamed_DoesNotReprocessTheTypeAsync()
    {
        // The control for the test above: the same save-then-Delta flow, with a change that is not worth a row.
        var recorded = await EditAndSaveRuleAsync(rule => rule.Name = "HR Import (renamed)");

        var run = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded, Is.Empty, "a rename changes no synchronisation outcome, so nothing is recorded");
            Assert.That(run.ProcessedCsoIds, Is.Empty, "and a Delta Synchronisation does not walk the type");
        }
    }

    [Test]
    public async Task DeltaSync_AfterAnImportRuleIsSavedUnchanged_DoesNotReprocessTheTypeAsync()
    {
        var recorded = await EditAndSaveRuleAsync(_ => { });

        var run = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded, Is.Empty);
            Assert.That(run.ProcessedCsoIds, Is.Empty);
        }
    }

    [Test]
    public async Task DeltaSync_SeveralSavesBeforeTheNextRun_ProcessTheTypeOnceAndApplyEveryOneAsync()
    {
        var first = await EditAndSaveRuleAsync(rule => rule.ObjectScopingCriteriaGroups.Add(EmployeesOnly(rule)));
        var second = await EditAndSaveRuleAsync(rule => rule.ObjectScopingCriteriaGroups.Clear());

        var run = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Has.Count.EqualTo(1));
            Assert.That(second, Has.Count.EqualTo(1));
            Assert.That(run.ProcessedCsoIds, Is.Unique.And.Count.EqualTo(3), "the type is processed once, not once per save");
            Assert.That(first.Concat(second).Select(c => c.AppliedAt), Has.All.Not.Null, "and both changes are applied by that one run");
            Assert.That(SyncRepo.ConnectedSystemObjects.Values.Select(c => c.MetaverseObjectId), Has.All.Not.Null,
                "the rule ends as it began: everyone is in scope");
        }
    }

    [Test]
    public async Task FullSync_AfterAnImportRulesScopeIsNarrowed_AppliesTheChangeTooAsync()
    {
        var recorded = await EditAndSaveRuleAsync(rule => rule.ObjectScopingCriteriaGroups.Add(EmployeesOnly(rule)));

        var run = await RunAsync(ConnectedSystemRunType.FullSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.ConnectedSystemObjects.Values.Single(c => DisplayNameOf(c) == "Carol").MetaverseObjectId, Is.Null);
            Assert.That(recorded.Single().ClaimedByActivityId, Is.EqualTo(run.Activity.Id));
            Assert.That(recorded.Single().AppliedAt, Is.Not.Null, "a Full Synchronisation applies the change as well");
        }
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Edits the rule and saves it as the portal and the REST API do, then mirrors the rows that save recorded into the
    /// in-memory synchronisation store (the harness keeps configuration and synchronisation state in two stores, where
    /// production has one database). Returns the mirrored rows: the changes awaiting synchronisation the save recorded.
    /// </summary>
    private async Task<List<ConfigurationChangeSynchronisation>> EditAndSaveRuleAsync(Action<SyncRule> edit)
    {
        SettleSynchronisationChanges();
        edit(_rule);

        Assert.That(await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(_rule, _administrator), Is.True, "the rule saves");

        var mirrored = new List<ConfigurationChangeSynchronisation>();
        foreach (var row in DbContext.ConfigurationChangeSynchronisations.AsNoTracking().OrderBy(c => c.Id).ToList().Where(c => _mirroredIds.Add(c.Id)))
        {
            mirrored.Add(SyncRepo.SeedConfigurationChangeSynchronisation(new ConfigurationChangeSynchronisation
            {
                ConnectedSystemId = row.ConnectedSystemId,
                ConnectedSystemObjectTypeId = row.ConnectedSystemObjectTypeId,
                RequestedAt = row.RequestedAt,
                ConfigurationChangeActivityId = row.ConfigurationChangeActivityId,
                SyncRuleId = row.SyncRuleId,
                Reason = row.Reason
            }));
        }

        return mirrored;
    }

    // Built on the attribute instance the rule holds, which is the one the DbContext tracks.
    private static SyncRuleScopingCriteriaGroup EmployeesOnly(SyncRule rule)
    {
        var title = rule.ConnectedSystemObjectType.Attributes.Single(a => a.Name == "Title");
        return new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria
                {
                    ConnectedSystemAttribute = title,
                    ConnectedSystemAttributeId = title.Id,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "Employee",
                    CaseSensitive = true
                }
            ]
        };
    }

    private static string? DisplayNameOf(ConnectedSystemObject cso) =>
        cso.AttributeValues.SingleOrDefault(av => av.Attribute?.Name == "DisplayName")?.StringValue;

    private void SeedCso(ConnectedSystemObjectType type, string employeeId, string displayName, string title)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = _hr.Id,
            TypeId = type.Id,
            Type = type,
            ConnectedSystem = SyncRepo.ConnectedSystems[_hr.Id],
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        };
        var externalId = type.Attributes.Single(a => a.IsExternalId);
        cso.ExternalIdAttributeId = externalId.Id;
        AddValue(cso, externalId, guidValue: Guid.NewGuid());
        AddValue(cso, type.Attributes.Single(a => a.Name == "EmployeeId"), employeeId);
        AddValue(cso, type.Attributes.Single(a => a.Name == "DisplayName"), displayName);
        AddValue(cso, type.Attributes.Single(a => a.Name == "Title"), title);
        SyncRepo.SeedConnectedSystemObject(cso);
    }

    private static void AddValue(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute, string? stringValue = null, Guid? guidValue = null) =>
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = attribute.Id, Attribute = attribute,
            StringValue = stringValue, GuidValue = guidValue
        });

    private static SyncRuleMapping ImportFlow(SyncRule rule, ConnectedSystemObjectType csoType, string csAttributeName, MetaverseAttribute target)
    {
        var source = csoType.Attributes.Single(a => a.Name == csAttributeName);
        return new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        };
    }

    /// <summary>
    /// The initiating user the audited configuration paths need. Created after the first synchronisation, so it first
    /// settles what that run modified on instances shared with the DbContext (the pattern of
    /// <c>ExportRuleConfigurationChangeWorkflowTests</c>).
    /// </summary>
    private async Task<MetaverseObject> NewAdministratorAsync()
    {
        SettleSynchronisationChanges();

        var administrator = new MetaverseObject
        {
            Id = Guid.NewGuid(), Type = DbContext.MetaverseObjectTypes.First(), Created = DateTime.UtcNow, CachedDisplayName = "Test Administrator"
        };
        DbContext.MetaverseObjects.Add(administrator);
        await DbContext.SaveChangesAsync();
        return administrator;
    }

    /// <summary>
    /// Settles what the synchronisation processors modified on instances shared with the DbContext, so a configuration
    /// save does not try to write them (the pattern of <c>ExportRuleConfigurationChangeWorkflowTests</c>).
    /// </summary>
    private void SettleSynchronisationChanges()
    {
        // Settling resets the shared Connected System to the values the DbContext last saw, which loses the watermark the
        // last run wrote; a run does not lose it in production, where there is one database.
        var watermark = _hr.LastSyncCompletedAt;
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified).ToList())
            entry.State = entry.Entity is SyncRule or SyncRuleMapping or SyncRuleMappingSource or ConnectedSystem
                or ConnectedSystemObjectType or ConnectedSystemObjectTypeAttribute or ConnectedSystemRunProfile
                ? EntityState.Unchanged
                : EntityState.Detached;
        _hr.LastSyncCompletedAt = watermark;
    }

    private sealed record RunResult(Activity Activity, List<Guid> ProcessedCsoIds);

    /// <summary>
    /// Runs a synchronisation of the HR system as the worker does: the ids awaiting synchronisation are read before the
    /// Connected System is loaded, and handed to the processor.
    /// </summary>
    private async Task<RunResult> RunAsync(ConnectedSystemRunType runType)
    {
        // The run profile below detaches whatever a run modified; settle it first, so the rule stays tracked for the next save.
        SettleSynchronisationChanges();
        var eligibleIds = await SyncRepo.GetConfigurationChangeSynchronisationIdsAwaitingAsync(_hr.Id);
        var reloaded = await ReloadEntityAsync(_hr);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{runType} {Guid.NewGuid():N}", runType);
        var activity = await CreateActivityAsync(reloaded.Id, profile, runType);
        var processed = new List<Guid>();
        void Record(ConnectedSystemObject cso) => processed.Add(cso.Id);

        if (runType == ConnectedSystemRunType.FullSynchronisation)
        {
            await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource(),
                configurationChangeIdsEligibleToClaim: eligibleIds) { OnConnectedSystemObjectProcessedInPass2 = Record }.PerformFullSyncAsync();
        }
        else
        {
            await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource(),
                configurationChangeIdsEligibleToClaim: eligibleIds) { OnConnectedSystemObjectProcessedInPass2 = Record }.PerformDeltaSyncAsync();
        }

        return new RunResult(activity, processed);
    }
}
