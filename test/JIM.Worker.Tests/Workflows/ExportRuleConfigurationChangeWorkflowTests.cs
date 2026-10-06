// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Issue #1925: a change to an export Synchronisation Rule's configuration must reach the Metaverse Objects it now
/// covers, or no longer covers, at the next synchronisation, even when none of their own values has changed. Export
/// evaluation used to be queued only by an inbound Attribute Flow changing a Metaverse Object, so a rule created,
/// re-enabled, switched to provisioning or re-scoped never reached a stable population, and the configuration
/// previews (which predict exactly this) disagreed with what synchronisation then did.
///
/// Every change here is saved through the application layer, the path the portal, REST API and PowerShell share, and
/// then an ordinary synchronisation of the source runs. Topology: an "HR" source projecting a Person (EmployeeId,
/// DisplayName, Department, and Title into the Metaverse's Type attribute) for Alice and Bob (Employees) and Carol
/// (a Contractor), and a "Directory" target.
/// </summary>
[TestFixture]
public class ExportRuleConfigurationChangeWorkflowTests : WorkflowTestBase
{
    #region Provisioning

    [Test]
    public async Task FullSync_AfterProvisioningIsSwitchedOn_ProvisionsObjectsAlreadyInScopeAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: false, provisioning: false);
        Assert.That(PendingExportsFor(ctx.Directory), Is.Empty, "arrange: the rule does not provision yet");

        ctx.ExportRule.ProvisionToConnectedSystem = true;
        await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.ExportRule, ctx.Administrator);
        await RunFullSyncAsync(ctx.Hr);

        Assert.That(CreatesFor(ctx), Is.EquivalentTo(new[] { "Alice", "Bob", "Carol" }));
    }

    [Test]
    public async Task DeltaSync_WithNothingNewAfterProvisioningIsSwitchedOn_ProvisionsObjectsAlreadyInScopeAsync()
    {
        // Any synchronisation applies it, not only a Full Synchronisation of the system the objects came from: a
        // Delta Synchronisation with nothing new to process is the cheapest synchronisation there is, and the one a
        // schedule runs most.
        var ctx = await SetUpAsync(directoryAccounts: false, provisioning: false);
        // Nothing changes in HR after this, so the Delta Synchronisation below finds nothing new to process.
        await Task.Delay(5);
        SyncRepo.ConnectedSystems[ctx.Hr.Id].LastSyncCompletedAt = DateTime.UtcNow;

        ctx.ExportRule.ProvisionToConnectedSystem = true;
        await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.ExportRule, ctx.Administrator);
        await RunDeltaSyncAsync(ctx.Hr);

        Assert.That(CreatesFor(ctx), Is.EquivalentTo(new[] { "Alice", "Bob", "Carol" }));
    }

    [Test]
    public async Task FullSync_AfterTheExportRuleIsReEnabled_ProvisionsObjectsAlreadyInScopeAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: false, provisioning: true, ruleEnabled: false);
        Assert.That(PendingExportsFor(ctx.Directory), Is.Empty, "arrange: a disabled rule provisions nothing");

        ctx.ExportRule.Enabled = true;
        await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.ExportRule, ctx.Administrator);
        await RunFullSyncAsync(ctx.Hr);

        Assert.That(CreatesFor(ctx), Is.EquivalentTo(new[] { "Alice", "Bob", "Carol" }));
    }

    [Test]
    public async Task FullSync_AfterAProvisioningExportRuleIsCreated_ProvisionsObjectsAlreadyInScopeAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: false, provisioning: false, withExportRule: false);

        var rule = new SyncRule
        {
            Name = "Directory Provisioning",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemId = ctx.Directory.Id,
            ConnectedSystem = await DbContext.ConnectedSystems.SingleAsync(cs => cs.Id == ctx.Directory.Id),
            ConnectedSystemObjectTypeId = ctx.DirectoryType.Id,
            ConnectedSystemObjectType = await DbContext.ConnectedSystemObjectTypes.SingleAsync(t => t.Id == ctx.DirectoryType.Id),
            MetaverseObjectTypeId = ctx.MvType.Id,
            MetaverseObjectType = await DbContext.MetaverseObjectTypes.SingleAsync(t => t.Id == ctx.MvType.Id),
            ProvisionToConnectedSystem = true,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect
        };
        AddExportFlow(rule, ctx.DirectoryType, "EmployeeId", ctx.MvEmployeeId);
        AddExportFlow(rule, ctx.DirectoryType, "DisplayName", ctx.MvDisplayName);
        Assert.That(await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, ctx.Administrator), Is.True, "arrange: the rule saves");
        SyncRepo.SeedSyncRule(await Jim.ConnectedSystems.GetSyncRuleAsync(rule.Id) ?? throw new InvalidOperationException("the rule saves"));
        await RunFullSyncAsync(ctx.Hr);

        Assert.That(CreatesFor(ctx), Is.EquivalentTo(new[] { "Alice", "Bob", "Carol" }));
    }

    [Test]
    public async Task FullSync_AfterScopingCriteriaAreWidened_ProvisionsObjectsNowInScopeAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: false, provisioning: true, scopedToEmployees: true);
        Assert.That(CreatesFor(ctx), Is.EquivalentTo(new[] { "Alice", "Bob" }), "arrange: only Employees are provisioned");

        ctx.ExportRule.ObjectScopingCriteriaGroups.Clear();
        await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.ExportRule, ctx.Administrator);
        await RunFullSyncAsync(ctx.Hr);

        Assert.That(CreatesFor(ctx), Is.EquivalentTo(new[] { "Alice", "Bob", "Carol" }), "Carol is now in scope");
    }

    #endregion

    #region Deprovisioning

    [Test]
    public async Task FullSync_AfterScopingCriteriaAreNarrowed_DeprovisionsObjectsNoLongerInScopeAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: true, provisioning: false);
        Assert.That(PendingExportsFor(ctx.Directory), Is.Empty, "arrange: every joined account is current");

        ctx.ExportRule.ObjectScopingCriteriaGroups.Add(EmployeesOnly(ctx));
        await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.ExportRule, ctx.Administrator);
        await RunFullSyncAsync(ctx.Hr);

        Assert.That(DeletesFor(ctx), Is.EqualTo(new[] { "Carol" }), "Carol left the rule's scope, and its Deprovisioning Action is Delete");
    }

    [TestCase(OutboundDeprovisionAction.Delete)]
    [TestCase(OutboundDeprovisionAction.Disconnect)]
    public async Task FullSync_AfterScopingCriteriaAreNarrowed_DeprovisionsWithChangeDetectionOffAsync(OutboundDeprovisionAction action)
    {
        // Deprovisioning saves through EF one object at a time (the Delete Pending Export, the disconnected object).
        // With automatic change detection on, that save also inserts every execution item the review batch has
        // queued on its Activity, and the batch's own bulk insert then fails on a duplicate key, failing the run.
        // The page flush has always switched detection off for exactly this; the review must too. Only PostgreSQL
        // enforces the key, so this store fails the write instead.
        var syncRepository = new ChangeDetectionAwareSyncRepository();
        SyncRepo = syncRepository;
        SyncRepo.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);

        var ctx = await SetUpAsync(directoryAccounts: true, provisioning: false, deprovisionAction: action);
        ctx.ExportRule.ObjectScopingCriteriaGroups.Add(EmployeesOnly(ctx));
        await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.ExportRule, ctx.Administrator);
        await RunFullSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(syncRepository.SavesWithChangeDetectionOn, Is.Empty);
            Assert.That(FlaggedForReview(ctx), Is.Empty, "the review completed");
        }
    }

    /// <summary>Records each single-object EF save the engine makes while automatic change detection is on.</summary>
    private sealed class ChangeDetectionAwareSyncRepository : JIM.InMemoryData.SyncRepository
    {
        private bool _autoDetectChanges = true;

        public List<string> SavesWithChangeDetectionOn { get; } = [];

        public override void SetAutoDetectChangesEnabled(bool enabled) => _autoDetectChanges = enabled;

        public override Task CreatePendingExportAsync(PendingExport pendingExport)
        {
            if (_autoDetectChanges)
                SavesWithChangeDetectionOn.Add($"{nameof(CreatePendingExportAsync)} ({pendingExport.ChangeType})");
            return base.CreatePendingExportAsync(pendingExport);
        }

        public override Task UpdateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
        {
            if (_autoDetectChanges)
                SavesWithChangeDetectionOn.Add(nameof(UpdateConnectedSystemObjectAsync));
            return base.UpdateConnectedSystemObjectAsync(connectedSystemObject);
        }
    }

    [Test]
    public async Task FullSync_AfterTheExportRuleIsDisabled_DeprovisionsNothingAsync()
    {
        // Disabling a rule stops it; it does not deprovision what it created (docs: "Nothing existing is destroyed").
        var ctx = await SetUpAsync(directoryAccounts: true, provisioning: false);

        ctx.ExportRule.Enabled = false;
        await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.ExportRule, ctx.Administrator);
        await RunFullSyncAsync(ctx.Hr);

        Assert.That(PendingExportsFor(ctx.Directory), Is.Empty);
    }

    #endregion

    #region What is not reviewed

    [Test]
    public async Task Save_ThatLeavesScopeAlone_FlagsNothingForReviewAsync()
    {
        // A review re-evaluates every object of the type, so a save that cannot move any object (a rename here) must
        // not cost one.
        var ctx = await SetUpAsync(directoryAccounts: true, provisioning: false);

        ctx.ExportRule.Name = "Directory Export (renamed)";
        await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.ExportRule, ctx.Administrator);

        Assert.That(FlaggedForReview(ctx), Is.Empty);
    }

    [Test]
    public async Task Save_ThatSwitchesProvisioningOff_FlagsNothingForReviewAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: false, provisioning: true);

        ctx.ExportRule.ProvisionToConnectedSystem = false;
        await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.ExportRule, ctx.Administrator);

        Assert.That(FlaggedForReview(ctx), Is.Empty);
    }

    #endregion

    #region A change made while a synchronisation runs

    [Test]
    public async Task FullSync_WhenTheRuleChangesWhileItRuns_LeavesTheReviewForTheNextSynchronisationAsync()
    {
        // A synchronisation evaluates against the rules it read at its start. Bob and his colleagues are flagged when
        // provisioning is switched on, then a further change lands while a run is reviewing them: clearing their flags
        // would leave that change applied to no one. The run keeps them, and the next one reviews and clears them.
        var syncRepository = new SaveDuringClearSyncRepository();
        SyncRepo = syncRepository;
        SyncRepo.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);

        var ctx = await SetUpAsync(directoryAccounts: false, provisioning: false);
        ctx.ExportRule.ProvisionToConnectedSystem = true;
        await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.ExportRule, ctx.Administrator);
        Assert.That(FlaggedForReview(ctx), Has.Count.EqualTo(3), "arrange: switching provisioning on flags everyone");

        syncRepository.BeforeNextClear = async () =>
        {
            SettleSynchronisationChanges();
            ctx.ExportRule.ObjectScopingCriteriaGroups.Add(EmployeesOnly(ctx));
            await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.ExportRule, ctx.Administrator);
        };
        await RunFullSyncAsync(ctx.Hr);

        Assert.That(FlaggedForReview(ctx), Has.Count.EqualTo(3), "the change landed mid-run, so the review is left for the next run");

        await RunDeltaSyncAsync(ctx.Hr);

        Assert.That(FlaggedForReview(ctx), Is.Empty, "the next run reviews against the rule as it now stands, and clears");
    }

    /// <summary>Runs a configuration change at the moment the scope review drain is about to clear its batch.</summary>
    private sealed class SaveDuringClearSyncRepository : JIM.InMemoryData.SyncRepository
    {
        public Func<Task>? BeforeNextClear { get; set; }

        public override async Task<bool> ClearMetaverseObjectScopeReviewPendingAsync(IReadOnlyCollection<Guid> ids, DateTime? exportRulesReadWatermark)
        {
            var beforeClear = BeforeNextClear;
            BeforeNextClear = null;
            if (beforeClear != null)
                await beforeClear();

            return await base.ClearMetaverseObjectScopeReviewPendingAsync(ids, exportRulesReadWatermark);
        }
    }

    #endregion

    #region Attribute Flow

    [Test]
    public async Task FullSync_OfTheTargetAfterAnExportAttributeFlowIsAdded_FlowsItToExistingObjectsAsync()
    {
        // A guard rather than a fix: a changed mapping already reaches every object the rule manages at the target's
        // next Full Synchronisation, through drift detection, with Enforce State on (the default). Scope review only
        // decides who is in and out of scope, so it is not what carries a mapping change; this pins the path that is.
        // The Directory accounts hold Department "Sales"; HR says Finance.
        var ctx = await SetUpAsync(directoryAccounts: true, provisioning: false);

        var directoryDepartment = ctx.DirectoryType.Attributes.Single(a => a.Name == "Department");
        await Jim.ConnectedSystems.CreateSyncRuleMappingAsync(new SyncRuleMapping
        {
            SyncRule = ctx.ExportRule,
            SyncRuleId = ctx.ExportRule.Id,
            TargetConnectedSystemAttribute = directoryDepartment,
            TargetConnectedSystemAttributeId = directoryDepartment.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = ctx.MvDepartment, MetaverseAttributeId = ctx.MvDepartment.Id } }
        }, ctx.Administrator);
        await RunFullSyncAsync(ctx.Directory);

        Assert.That(PendingExportsFor(ctx.Directory).SelectMany(pe => pe.AttributeValueChanges)
                .Where(c => c.AttributeId == directoryDepartment.Id).Select(c => c.StringValue).ToList(),
            Is.EqualTo(new[] { "Finance", "Finance", "Finance" }), "every account the rule manages gets the newly flowed value");
    }

    #endregion

    #region Topology

    private sealed record Context(
        ConnectedSystem Hr,
        ConnectedSystem Directory,
        ConnectedSystemObjectType DirectoryType,
        MetaverseObjectType MvType,
        MetaverseAttribute MvEmployeeId,
        MetaverseAttribute MvDisplayName,
        MetaverseAttribute MvTitle,
        MetaverseAttribute MvDepartment,
        SyncRule ExportRule,
        MetaverseObject Administrator);

    /// <param name="directoryAccounts">Seed a joined, current Directory account for each person (no provisioning needed).</param>
    /// <param name="provisioning">Whether the export rule provisions.</param>
    /// <param name="ruleEnabled">Whether the export rule starts enabled.</param>
    /// <param name="scopedToEmployees">Whether the export rule starts scoped to Type = Employee.</param>
    /// <param name="withExportRule">False to build the topology with no export rule at all.</param>
    /// <param name="deprovisionAction">The export rule's Deprovisioning Action.</param>
    private async Task<Context> SetUpAsync(bool directoryAccounts, bool provisioning, bool ruleEnabled = true,
        bool scopedToEmployees = false, bool withExportRule = true, OutboundDeprovisionAction deprovisionAction = OutboundDeprovisionAction.Delete)
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

        SyncRule? exportRule = null;
        if (withExportRule)
        {
            exportRule = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Export",
                enableProvisioning: provisioning, deprovisionAction: deprovisionAction);
            exportRule.Enabled = ruleEnabled;
            AddExportFlow(exportRule, directoryType, "EmployeeId", mvEmployeeId);
            AddExportFlow(exportRule, directoryType, "DisplayName", mvDisplayName);
        }

        await DbContext.SaveChangesAsync();

        SeedCso(hr, hrType, "E1", "Alice", "Employee", "Finance");
        SeedCso(hr, hrType, "E2", "Bob", "Employee", "Finance");
        SeedCso(hr, hrType, "E3", "Carol", "Contractor", "Finance");
        if (directoryAccounts)
        {
            SeedCso(directory, directoryType, "E1", "Alice", "Employee", "Sales");
            SeedCso(directory, directoryType, "E2", "Bob", "Employee", "Sales");
            SeedCso(directory, directoryType, "E3", "Carol", "Contractor", "Sales");
        }

        var context = new Context(hr, directory, directoryType, mvType, mvEmployeeId, mvDisplayName, mvTitle, mvDepartment, null!, null!);
        if (exportRule != null && scopedToEmployees)
            exportRule.ObjectScopingCriteriaGroups.Add(EmployeesOnly(context));
        await DbContext.SaveChangesAsync();

        await RunFullSyncAsync(hr);
        if (directoryAccounts)
        {
            await RunFullSyncAsync(directory);
            Assert.That(SyncRepo.ConnectedSystemObjects.Values.Where(c => c.ConnectedSystemId == directory.Id).Select(c => c.MetaverseObjectId),
                Has.All.Not.Null, "arrange: every Directory account is joined");
        }

        var administrator = await NewAdministratorAsync();

        // The harness detaches what the synchronisations modified, the export rule included, so the DbContext and the
        // in-memory synchronisation store would each hold their own copy. Load it back through the configuration side
        // and hand that tracked instance to the synchronisation store too: one rule, as in production.
        SyncRule trackedExportRule = null!;
        if (exportRule != null)
        {
            trackedExportRule = await Jim.ConnectedSystems.GetSyncRuleAsync(exportRule.Id)
                ?? throw new InvalidOperationException("arrange: the export rule reloads");
            SyncRepo.SeedSyncRule(trackedExportRule);
        }

        return context with { ExportRule = trackedExportRule, Administrator = administrator };
    }

    private static SyncRuleScopingCriteriaGroup EmployeesOnly(Context ctx) => new()
    {
        Type = SearchGroupType.All,
        Criteria = new List<SyncRuleScopingCriteria>
        {
            new() { MetaverseAttribute = ctx.MvTitle, MetaverseAttributeId = ctx.MvTitle.Id, ComparisonType = SearchComparisonType.Equals, StringValue = "Employee", CaseSensitive = true }
        }
    };

    /// <summary>
    /// The initiating user the audited configuration paths need. Created after the synchronisations, so it first
    /// settles what they modified.
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
    /// save does not try to write them (the pattern of <c>StalePendingExportLifecycleWorkflowTests</c>).
    /// </summary>
    private void SettleSynchronisationChanges()
    {
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified).ToList())
            entry.State = entry.Entity is SyncRule or SyncRuleMapping or SyncRuleMappingSource or ConnectedSystem
                or ConnectedSystemObjectType or ConnectedSystemObjectTypeAttribute or ConnectedSystemRunProfile
                ? EntityState.Unchanged
                : EntityState.Detached;
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

    private static void AddExportFlow(SyncRule rule, ConnectedSystemObjectType csoType, string csAttributeName, MetaverseAttribute source)
    {
        var target = csoType.Attributes.Single(a => a.Name == csAttributeName);
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
        });
    }

    private void SeedCso(ConnectedSystem system, ConnectedSystemObjectType type, string employeeId, string displayName, string title, string department)
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
        AddValue(cso, type.Attributes.Single(a => a.Name == "Department"), department);
        SyncRepo.SeedConnectedSystemObject(cso);
    }

    private static void AddValue(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute, string? stringValue = null, Guid? guidValue = null) =>
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = attribute.Id, Attribute = attribute,
            StringValue = stringValue, GuidValue = guidValue
        });

    /// <summary>The people flagged for export scope review, by display name.</summary>
    private List<string?> FlaggedForReview(Context ctx) => SyncRepo.MetaverseObjects.Values
        .Where(mvo => mvo.Type?.Id == ctx.MvType.Id && mvo.ScopeReviewPending)
        .Select(mvo => mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDisplayName.Id)?.StringValue)
        .ToList();

    private List<PendingExport> PendingExportsFor(ConnectedSystem system) =>
        SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == system.Id).ToList();

    /// <summary>The people a Create is staged for in the Directory, by the DisplayName it would write.</summary>
    private List<string?> CreatesFor(Context ctx) => PendingExportsFor(ctx.Directory)
        .Where(pe => pe.ChangeType == PendingExportChangeType.Create)
        .Select(pe => pe.AttributeValueChanges.SingleOrDefault(c => c.Attribute?.Name == "DisplayName")?.StringValue)
        .ToList();

    /// <summary>The people a Delete is staged for in the Directory, by their account's DisplayName.</summary>
    private List<string?> DeletesFor(Context ctx) => PendingExportsFor(ctx.Directory)
        .Where(pe => pe.ChangeType == PendingExportChangeType.Delete)
        .Select(pe => SyncRepo.ConnectedSystemObjects[pe.ConnectedSystemObjectId!.Value].AttributeValues
            .SingleOrDefault(av => av.Attribute?.Name == "DisplayName")?.StringValue)
        .ToList();

    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }

    private async Task RunDeltaSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();
    }

    #endregion
}
