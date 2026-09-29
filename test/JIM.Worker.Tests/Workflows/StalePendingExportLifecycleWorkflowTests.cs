// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A queued Pending Export change outliving the reason it was queued. Each test queues an ordinary Update for a
/// joined account (a DisplayName change), then removes the reason for it before any export runs, then exports:
/// the account leaves the export rule's scope with the Disconnect action (the join is broken and JIM stops
/// managing the account), or the export Attribute Flow that queued the change is removed or disabled, or its
/// Synchronisation Rule is disabled. Topology: an "HR" source projecting a Person (EmployeeId, DisplayName,
/// Department, and Title into the Metaverse's Type attribute), and a "Directory" target joined by EmployeeId whose
/// export rule, scoped to Type = "Employee", flows DisplayName and Department, with no provisioning and the
/// Disconnect Deprovisioning Action.
/// </summary>
[TestFixture]
public class StalePendingExportLifecycleWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task Export_WithNothingChangedSinceTheChangeWasQueued_WritesTheQueuedChangeAsync()
    {
        // Control: the topology exports the queued change when its reason still stands, so the tests below fail
        // for the reason they name rather than because nothing is ever exported here.
        var ctx = await SetUpWithQueuedDisplayNameChangeAsync();

        var connector = new MockCallConnector();
        await RunExportAsync(ctx.Directory, connector);

        Assert.That(connector.ExportedItems.SelectMany(pe => pe.AttributeValueChanges).Select(c => (c.Attribute.Name, c.StringValue)),
            Has.Member(("DisplayName", "Alicia")));
    }

    [Test]
    public async Task Export_AfterOneOfTwoExportAttributeFlowsWasRemoved_WritesOnlyTheChangeStillFlowedAsync()
    {
        var ctx = await SetUpWithQueuedDisplayNameChangeAsync();
        await QueueDepartmentChangeAsync(ctx, "Finance");

        ctx.ExportRule.AttributeFlowRules.Remove(ctx.DisplayNameExportMapping);

        var connector = new MockCallConnector();
        await RunExportAsync(ctx.Directory, connector);

        Assert.That(connector.ExportedItems.SelectMany(pe => pe.AttributeValueChanges).Select(c => (c.Attribute.Name, c.StringValue)),
            Is.EquivalentTo(new[] { ("Department", "Finance") }),
            "the change whose Attribute Flow still exists is written; the other is withdrawn");
        AssertNoDisplayNameChangeQueued(ctx);
    }

    [Test]
    public async Task Export_AfterTheAccountLeftScopeWithDisconnect_DoesNotWriteTheQueuedChangeAsync()
    {
        var ctx = await SetUpWithQueuedDisplayNameChangeAsync();

        SetValue(ctx.HrCso, "Title", "Leaver");
        await ModifyCsoAsync(ctx.HrCso);
        await RunFullSyncAsync(ctx.Hr);
        Assert.That(ctx.DirectoryCso.MetaverseObjectId, Is.Null, "precondition: leaving scope disconnected the account");

        var connector = new MockCallConnector();
        await RunExportAsync(ctx.Directory, connector);

        Assert.That(connector.ExportedItems.SelectMany(pe => pe.AttributeValueChanges).Select(c => c.Attribute.Name), Has.None.EqualTo("DisplayName"),
            "JIM no longer manages the account, so the change queued while it did must not be written to it");
        AssertNoDisplayNameChangeQueued(ctx);
    }

    [Test]
    public async Task Export_AfterTheExportAttributeFlowWasRemoved_DoesNotWriteTheQueuedChangeAsync()
    {
        var ctx = await SetUpWithQueuedDisplayNameChangeAsync();

        ctx.ExportRule.AttributeFlowRules.Remove(ctx.DisplayNameExportMapping);

        var connector = new MockCallConnector();
        await RunExportAsync(ctx.Directory, connector);

        Assert.That(connector.ExportedItems.SelectMany(pe => pe.AttributeValueChanges).Select(c => c.Attribute.Name), Has.None.EqualTo("DisplayName"),
            "the Attribute Flow that queued the change no longer exists");
        AssertNoDisplayNameChangeQueued(ctx);
    }

    [Test]
    public async Task Export_AfterTheExportAttributeFlowWasDisabled_DoesNotWriteTheQueuedChangeAsync()
    {
        var ctx = await SetUpWithQueuedDisplayNameChangeAsync();

        ctx.DisplayNameExportMapping.Enabled = false;

        var connector = new MockCallConnector();
        var activity = await RunExportAsync(ctx.Directory, connector);

        Assert.That(activity.WarningMessage, Does.StartWith("1 queued change was withdrawn instead of exported"),
            "the withdrawal is reported on the export's Activity, not just logged");

        Assert.That(connector.ExportedItems.SelectMany(pe => pe.AttributeValueChanges).Select(c => c.Attribute.Name), Has.None.EqualTo("DisplayName"),
            "a disabled Attribute Flow flows nothing");
        AssertNoDisplayNameChangeQueued(ctx);
    }

    [Test]
    public async Task Export_AfterTheExportSynchronisationRuleWasDisabled_DoesNotWriteTheQueuedChangeAsync()
    {
        var ctx = await SetUpWithQueuedDisplayNameChangeAsync();

        ctx.ExportRule.Enabled = false;

        var connector = new MockCallConnector();
        await RunExportAsync(ctx.Directory, connector);

        Assert.That(connector.ExportedItems.SelectMany(pe => pe.AttributeValueChanges).Select(c => c.Attribute.Name), Has.None.EqualTo("DisplayName"),
            "a disabled Synchronisation Rule is skipped by the synchronisation engine");
        AssertNoDisplayNameChangeQueued(ctx);
    }

    // ---- At configuration save: the queue reflects the change at once, not only at the next export ----

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_DisablingTheExportAttributeFlow_WithdrawsItsQueuedChangeAtOnceAsync()
    {
        var ctx = await SetUpWithQueuedDisplayNameChangeAsync();

        await Jim.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(ctx.DisplayNameExportMapping.Id,
            new SyncRuleMappingSettingsUpdate { Enabled = false }, ctx.Administrator);

        AssertNoDisplayNameChangeQueued(ctx);
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_DisablingAnotherAttributeFlow_KeepsTheQueuedChangeAsync()
    {
        var ctx = await SetUpWithQueuedDisplayNameChangeAsync();
        var departmentMapping = ctx.ExportRule.AttributeFlowRules.Single(m => m != ctx.DisplayNameExportMapping);

        await Jim.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(departmentMapping.Id,
            new SyncRuleMappingSettingsUpdate { Enabled = false }, ctx.Administrator);

        Assert.That(QueuedAttributeNames(ctx), Is.EqualTo(new[] { "DisplayName" }),
            "only a change the configuration change left without authority is withdrawn");
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_DisablingTheExportAttributeFlow_WithdrawsItsQueuedChangeAtOnceAsync()
    {
        var ctx = await SetUpWithQueuedDisplayNameChangeAsync();

        ctx.DisplayNameExportMapping.Enabled = false;
        await Jim.ConnectedSystems.UpdateSyncRuleMappingAsync(ctx.DisplayNameExportMapping, ctx.Administrator);

        AssertNoDisplayNameChangeQueued(ctx);
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_TheExportAttributeFlow_WithdrawsItsQueuedChangeAtOnceAsync()
    {
        var ctx = await SetUpWithQueuedDisplayNameChangeAsync();

        await Jim.ConnectedSystems.DeleteSyncRuleMappingAsync(ctx.DisplayNameExportMapping, ctx.Administrator);

        AssertNoDisplayNameChangeQueued(ctx);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_DisablingTheExportRule_WithdrawsItsQueuedChangeAtOnceAsync()
    {
        var ctx = await SetUpWithQueuedDisplayNameChangeAsync();

        ctx.ExportRule.Enabled = false;
        await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.ExportRule, ctx.Administrator);

        AssertNoDisplayNameChangeQueued(ctx);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_SavingTheExportRuleUnchanged_KeepsTheQueuedChangeAsync()
    {
        var ctx = await SetUpWithQueuedDisplayNameChangeAsync();

        await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.ExportRule, ctx.Administrator);

        Assert.That(QueuedAttributeNames(ctx), Is.EqualTo(new[] { "DisplayName" }));
    }

    [Test]
    public async Task DeleteSyncRuleAsync_TheExportRule_WithdrawsItsQueuedChangeAtOnceAsync()
    {
        var ctx = await SetUpWithQueuedDisplayNameChangeAsync();

        // The deletion is written to the DbContext only; in production the synchronisation side reads the same
        // database, so mirror it into the in-memory store first. Nothing reads that store in between except the
        // withdrawal under test, and without it the queued change stays put.
        SyncRepo.RemoveSyncRule(ctx.ExportRule.Id);
        await Jim.ConnectedSystems.DeleteSyncRuleAsync(ctx.ExportRule, ctx.Administrator);

        AssertNoDisplayNameChangeQueued(ctx);
    }

    // ---- Topology ----

    private sealed record Context(
        ConnectedSystem Hr,
        ConnectedSystem Directory,
        ConnectedSystemObject HrCso,
        ConnectedSystemObject DirectoryCso,
        SyncRule ExportRule,
        SyncRuleMapping DisplayNameExportMapping,
        MetaverseObject Administrator);

    /// <summary>
    /// Changes HR's Department and synchronises, queuing a second change (for Department, whose export Attribute Flow
    /// is left alone) alongside the DisplayName one.
    /// </summary>
    private async Task QueueDepartmentChangeAsync(Context ctx, string department)
    {
        SetValue(ctx.HrCso, "Department", department);
        await ModifyCsoAsync(ctx.HrCso);
        await RunFullSyncAsync(ctx.Hr);
        Assert.That(PendingExportsFor(ctx.Directory).SelectMany(pe => pe.AttributeValueChanges).Select(c => c.StringValue),
            Is.EquivalentTo(new[] { "Alicia", department }), "arrange: both changes are queued for the account");
    }

    private List<string> QueuedAttributeNames(Context ctx) =>
        PendingExportsFor(ctx.Directory).SelectMany(pe => pe.AttributeValueChanges)
            .Where(c => c.Status is PendingExportAttributeChangeStatus.Pending or PendingExportAttributeChangeStatus.ExportedNotConfirmed)
            .Select(c => c.Attribute.Name)
            .ToList();

    /// <summary>
    /// The initiating user the audited configuration paths need. Created after the synchronisations, so it first
    /// settles what the processors modified on instances shared with the DbContext (the WorkflowTestBase pattern):
    /// objects whose rows were never written to the DbContext are detached, since saving them would fail, while the
    /// configuration the DbContext does hold stays tracked as it is, so the configuration paths under test mutate
    /// the very instances the synchronisation side reads (in production both are one database).
    /// </summary>
    private async Task<MetaverseObject> NewAdministratorAsync()
    {
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified).ToList())
            entry.State = entry.Entity is SyncRule or SyncRuleMapping or SyncRuleMappingSource or ConnectedSystem
                or ConnectedSystemObjectType or ConnectedSystemObjectTypeAttribute or ConnectedSystemRunProfile
                ? EntityState.Unchanged
                : EntityState.Detached;

        var administrator = new MetaverseObject
        {
            Id = Guid.NewGuid(), Type = DbContext.MetaverseObjectTypes.First(), Created = DateTime.UtcNow, CachedDisplayName = "Test Administrator"
        };
        DbContext.MetaverseObjects.Add(administrator);
        await DbContext.SaveChangesAsync();
        return administrator;
    }

    private void AssertNoDisplayNameChangeQueued(Context ctx) =>
        Assert.That(PendingExportsFor(ctx.Directory).SelectMany(pe => pe.AttributeValueChanges)
                .Where(c => c.Status is PendingExportAttributeChangeStatus.Pending or PendingExportAttributeChangeStatus.ExportedNotConfirmed)
                .Select(c => c.Attribute.Name),
            Has.None.EqualTo("DisplayName"), "the withdrawn change no longer sits in the queue either");

    /// <summary>
    /// Builds the topology, synchronises both systems so the Directory account is joined and current, then changes
    /// HR's DisplayName and synchronises again so a DisplayName Update is queued for the account, not yet exported.
    /// </summary>
    private async Task<Context> SetUpWithQueuedDisplayNameChangeAsync()
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvTitle = mvType.Attributes.First(a => a.Name == "Type");
        var mvDepartment = new MetaverseAttribute
        {
            Name = "Department",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvDepartment);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvDepartment);

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User", Attributes());
        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "User", Attributes());

        var hrImport = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        AddImportFlow(hrImport, hrType, "EmployeeId", mvEmployeeId);
        AddImportFlow(hrImport, hrType, "DisplayName", mvDisplayName);
        AddImportFlow(hrImport, hrType, "Title", mvTitle);
        AddImportFlow(hrImport, hrType, "Department", mvDepartment);

        var directoryImport = await CreateImportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Import", enableProjection: false);
        AddImportFlow(directoryImport, directoryType, "EmployeeId", mvEmployeeId);
        var directoryEmployeeId = directoryType.Attributes.Single(a => a.Name == "EmployeeId");
        directoryImport.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = directoryImport,
            SyncRuleId = directoryImport.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeId,
            TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = directoryEmployeeId, ConnectedSystemAttributeId = directoryEmployeeId.Id } }
        });

        var exportRule = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Export",
            enableProvisioning: false, deprovisionAction: OutboundDeprovisionAction.Disconnect);
        var directoryDisplayName = directoryType.Attributes.Single(a => a.Name == "DisplayName");
        var displayNameMapping = new SyncRuleMapping
        {
            SyncRule = exportRule,
            SyncRuleId = exportRule.Id,
            TargetConnectedSystemAttribute = directoryDisplayName,
            TargetConnectedSystemAttributeId = directoryDisplayName.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvDisplayName, MetaverseAttributeId = mvDisplayName.Id } }
        };
        exportRule.AttributeFlowRules.Add(displayNameMapping);
        var directoryDepartment = directoryType.Attributes.Single(a => a.Name == "Department");
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            SyncRuleId = exportRule.Id,
            TargetConnectedSystemAttribute = directoryDepartment,
            TargetConnectedSystemAttributeId = directoryDepartment.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvDepartment, MetaverseAttributeId = mvDepartment.Id } }
        });
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new() { MetaverseAttribute = mvTitle, ComparisonType = SearchComparisonType.Equals, StringValue = "Employee", CaseSensitive = true }
            }
        });
        await DbContext.SaveChangesAsync();

        var hrCso = SeedCso(hr, hrType, "E1", "Alice", "Employee");
        var directoryCso = SeedCso(directory, directoryType, "E1", "Alice", "Employee");
        await RunFullSyncAsync(hr);
        await RunFullSyncAsync(directory);
        Assert.That(directoryCso.MetaverseObjectId, Is.Not.Null, "arrange: the Directory account is joined");
        Assert.That(PendingExportsFor(directory), Is.Empty, "arrange: the joined account is already current");

        SetValue(hrCso, "DisplayName", "Alicia");
        await ModifyCsoAsync(hrCso);
        await RunFullSyncAsync(hr);
        Assert.That(PendingExportsFor(directory).SelectMany(pe => pe.AttributeValueChanges).Select(c => c.StringValue), Is.EqualTo(new[] { "Alicia" }),
            "arrange: the DisplayName change is queued for the account");

        var administrator = await NewAdministratorAsync();

        // The harness detaches what the synchronisations modified, the export rule included, so the DbContext and the
        // in-memory synchronisation store would each hold their own copy of the rule. Load it back through the
        // configuration side and hand that tracked instance to the synchronisation store too: one rule, as in
        // production, where both read the same database.
        var trackedExportRule = await Jim.ConnectedSystems.GetSyncRuleAsync(exportRule.Id)
            ?? throw new InvalidOperationException("arrange: the export rule reloads");
        SyncRepo.SeedSyncRule(trackedExportRule);
        var trackedDisplayNameMapping = trackedExportRule.AttributeFlowRules.Single(m => m.Id == displayNameMapping.Id);

        return new Context(hr, directory, hrCso, directoryCso, trackedExportRule, trackedDisplayNameMapping, administrator);
    }

    private static List<ConnectedSystemObjectTypeAttribute> Attributes() =>
    [
        new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
        new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "Title", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "Department", Type = AttributeDataType.Text, Selected = true }
    ];

    private static void AddImportFlow(SyncRule rule, ConnectedSystemObjectType csoType, string csAttributeName, MetaverseAttribute target)
    {
        var source = csoType.Attributes.Single(a => a.Name == csAttributeName);
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        });
    }

    private ConnectedSystemObject SeedCso(ConnectedSystem system, ConnectedSystemObjectType type, string employeeId, string displayName, string title)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            TypeId = type.Id,
            Type = type,
            ConnectedSystem = SyncRepo.ConnectedSystems[system.Id],
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        };
        var externalId = type.Attributes.Single(a => a.IsExternalId);
        cso.ExternalIdAttributeId = externalId.Id;
        AddValue(cso, externalId, guidValue: Guid.NewGuid());
        AddValue(cso, type.Attributes.Single(a => a.Name == "EmployeeId"), employeeId);
        AddValue(cso, type.Attributes.Single(a => a.Name == "DisplayName"), displayName);
        AddValue(cso, type.Attributes.Single(a => a.Name == "Title"), title);
        AddValue(cso, type.Attributes.Single(a => a.Name == "Department"), "Sales");
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static void AddValue(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute, string? stringValue = null, Guid? guidValue = null) =>
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = attribute.Id, Attribute = attribute,
            StringValue = stringValue, GuidValue = guidValue
        });

    private static void SetValue(ConnectedSystemObject cso, string name, string value) =>
        cso.AttributeValues.Single(av => av.Attribute.Name == name).StringValue = value;

    private List<PendingExport> PendingExportsFor(ConnectedSystem system) =>
        SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == system.Id).ToList();

    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }

    private async Task<Activity> RunExportAsync(ConnectedSystem system, MockCallConnector connector)
    {
        var runProfile = await CreateRunProfileAsync(system.Id, "Export", ConnectedSystemRunType.Export);
        var reloaded = await ReloadEntityAsync(system);
        var activity = await CreateActivityAsync(reloaded.Id, runProfile, ConnectedSystemRunType.Export);
        var workerTask = new SynchronisationWorkerTask(reloaded.Id, runProfile.Id) { Id = Guid.NewGuid(), Status = WorkerTaskStatus.Processing, Activity = activity };
        await new SyncExportTaskProcessor(new SyncServer(Jim), SyncRepo, connector, reloaded, runProfile, workerTask, new CancellationTokenSource())
            .PerformExportAsync();
        DbContext.ChangeTracker.Clear();
        return activity;
    }
}
