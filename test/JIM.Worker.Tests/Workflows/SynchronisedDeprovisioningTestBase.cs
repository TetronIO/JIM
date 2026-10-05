// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Shared fixtures for Connected System Synchronised Deprovisioning (#809) and its preview (#134): the topologies, the
/// fence, the export target and the synchronisation that seeds them. One set, so the preview is always asked about exactly
/// the state the real run is tested against, which is what lets a test run both and compare them.
/// </summary>
public abstract class SynchronisedDeprovisioningTestBase : WorkflowTestBase
{
    protected const string HrDescription = "HR Description";
    protected const string TrainingDescription = "Training Description";
    protected const string SharedEmployeeId = "EMP001";

    protected FailingSyncRepository FailingSyncRepo = null!;

    /// <summary>
    /// In-memory sync repository whose Metaverse Object batch update can be made to throw, simulating a
    /// database failure partway through the deprovisioning run.
    /// </summary>
    protected sealed class FailingSyncRepository : JIM.InMemoryData.SyncRepository
    {
        public bool ThrowOnUpdateMetaverseObjects { get; set; }

        public override Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
        {
            if (ThrowOnUpdateMetaverseObjects)
                throw new InvalidOperationException("Simulated database failure during the deprovisioning batch.");
            return base.UpdateMetaverseObjectsAsync(metaverseObjects);
        }
    }

    [SetUp]
    public void SetUpFailableSyncRepo()
    {
        // Replace the base harness's sync repository with the failable twin BEFORE any seeding, so every
        // test (not just the failure one) runs against the same repository instance the helpers seed.
        // The base Jim instance is NOT disposed here: disposing it would dispose the shared repository and
        // DbContext this replacement wraps; base tear-down disposes the replacement, which owns them both.
        FailingSyncRepo = new FailingSyncRepository();
        FailingSyncRepo.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        SyncRepo = FailingSyncRepo;
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);
    }

    protected sealed record DeprovisioningContext(
        ConnectedSystem Hr,
        ConnectedSystem? Training,
        SyncRule HrImportRule,
        int TrainingImportRuleId,
        int MvDescriptionAttributeId,
        int MvDisplayNameAttributeId,
        ConnectedSystem Target,
        ConnectedSystemObjectTypeAttribute TargetDescriptionAttribute,
        ConnectedSystemObjectTypeAttribute TargetDisplayNameAttribute);

    protected static MetaverseObjectAttributeValue? GetAttributeValue(MetaverseObject mvo, int attributeId) =>
        mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == attributeId && !av.NullValue);

    protected async Task<MetaverseObject> CreateAdministratorAsync()
    {
        var mvType = await CreateMvObjectTypeAsync("Administrator");
        var user = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvType,
            Created = DateTime.UtcNow,
            CachedDisplayName = "Test Administrator",
            Origin = MetaverseObjectOrigin.Internal
        };
        DbContext.MetaverseObjects.Add(user);
        await DbContext.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// Fences the system with the Deleting status, exactly as the queue step does, without building a task:
    /// the state a failed deprovisioning run leaves behind once the worker's boundary has deleted its task row.
    /// </summary>
    protected async Task FenceSystemAsync(ConnectedSystem system)
    {
        // Detach processor-modified entities first (the same guard the base harness's helpers apply): the
        // full syncs above leave tracked entities in states the in-memory store no longer recognises.
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Modified).ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        var persistedSystem = await DbContext.ConnectedSystems.FindAsync(system.Id);
        persistedSystem!.Status = ConnectedSystemStatus.Deleting;
        await DbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Fences the system with the Deleting status (as the queue step does) and builds the worker task with
    /// its Activity, mirroring what TaskingServer records at queue time.
    /// </summary>
    protected async Task<(DeleteConnectedSystemWorkerTask Task, Activity Activity)> FenceSystemAndBuildTaskAsync(ConnectedSystem system)
    {
        await FenceSystemAsync(system);

        var activity = new Activity
        {
            TargetName = system.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Deprovision,
            Status = ActivityStatus.InProgress,
            Executed = DateTime.UtcNow
            // ConnectedSystemId deliberately not set: the system is deleted before the Activity completes.
        };
        DbContext.Activities.Add(activity);
        await DbContext.SaveChangesAsync();

        var task = new DeleteConnectedSystemWorkerTask(system.Id, evaluateMvoDeletionRules: true, deleteChangeHistory: false)
        {
            SynchronisedDeprovisioning = true,
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = Guid.NewGuid(),
            InitiatedByName = "Test Administrator",
            Activity = activity
        };
        return (task, activity);
    }

    /// <summary>
    /// Sole-contributor topology plus a downstream export target: HR projects and flows DisplayName,
    /// EmployeeId and Description; a target system maps DisplayName and Description outbound.
    /// </summary>
    protected async Task<DeprovisioningContext> SetUpSoleContributorWithExportTargetAsync(bool exportScopedOnDescription = false,
        OutboundDeprovisionAction scopeExitAction = OutboundDeprovisionAction.Delete)
    {
        var (hrSystem, hrImportRule, mvType, mvDescriptionAttr, mvDisplayNameAttr) = await SetUpHrContributorAsync();
        var target = await AddExportTargetAsync(mvType, mvDisplayNameAttr, mvDescriptionAttr, exportScopedOnDescription, scopeExitAction);
        return new DeprovisioningContext(hrSystem, null, hrImportRule, 0, mvDescriptionAttr.Id,
            mvDisplayNameAttr.Id, target.System, target.DescriptionAttribute, target.DisplayNameAttribute);
    }

    /// <summary>
    /// Two-contributor topology plus a downstream export target: HR (Description priority 1, projects) and
    /// Training (Description priority 2, joins on EmployeeId).
    /// </summary>
    protected async Task<DeprovisioningContext> SetUpTwoContributorsWithExportTargetAsync(string trainingDescription = TrainingDescription)
    {
        var (hrSystem, hrImportRule, mvType, mvDescriptionAttr, mvDisplayNameAttr) = await SetUpHrContributorAsync();
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var trainingSystem = await CreateConnectedSystemAsync("Training Source");
        var trainingExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var trainingEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var trainingDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "TrainingDescription", Type = AttributeDataType.Text, Selected = true };
        var trainingType = await CreateCsoTypeAsync(trainingSystem.Id, "TrainingRecord",
            new List<ConnectedSystemObjectTypeAttribute> { trainingExternalIdAttr, trainingEmployeeIdAttr, trainingDescriptionAttr });

        var trainingImportRule = await CreateImportSyncRuleAsync(trainingSystem.Id, trainingType, mvType, "Training Import", enableProjection: false);
        trainingImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(trainingImportRule, mvDescriptionAttr, trainingDescriptionAttr, priority: 2));
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

        var trainingCso = await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", SharedEmployeeId);
        trainingCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = trainingDescriptionAttr.Id, Attribute = trainingDescriptionAttr, StringValue = trainingDescription, ConnectedSystemObject = trainingCso
        });

        var target = await AddExportTargetAsync(mvType, mvDisplayNameAttr, mvDescriptionAttr);
        return new DeprovisioningContext(hrSystem, trainingSystem, hrImportRule, trainingImportRule.Id,
            mvDescriptionAttr.Id, mvDisplayNameAttr.Id, target.System, target.DescriptionAttribute, target.DisplayNameAttribute);
    }

    /// <summary>
    /// Sole-contributor topology with TWO HR objects (distinct Metaverse Objects) and an export target, for
    /// the resume-from-checkpoint test.
    /// </summary>
    protected async Task<DeprovisioningContext> SetUpTwoObjectsWithExportTargetAsync()
    {
        var (hrSystem, hrImportRule, mvType, mvDescriptionAttr, mvDisplayNameAttr) = await SetUpHrContributorAsync();

        var hrType = SyncRepo.ConnectedSystemObjects.Values
            .First(c => c.ConnectedSystemId == hrSystem.Id).Type;
        var hrDescriptionAttr = hrType.Attributes.First(a => a.Name == "HrDescription");
        var secondCso = await CreateCsoAsync(hrSystem.Id, hrType, "Jane Doe", "EMP002");
        secondCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrDescriptionAttr.Id, Attribute = hrDescriptionAttr, StringValue = HrDescription, ConnectedSystemObject = secondCso
        });

        var target = await AddExportTargetAsync(mvType, mvDisplayNameAttr, mvDescriptionAttr);
        return new DeprovisioningContext(hrSystem, null, hrImportRule, 0, mvDescriptionAttr.Id,
            mvDisplayNameAttr.Id, target.System, target.DescriptionAttribute, target.DisplayNameAttribute);
    }

    protected async Task<(ConnectedSystem HrSystem, SyncRule HrImportRule, MetaverseObjectType MvType, MetaverseAttribute MvDescriptionAttr, MetaverseAttribute MvDisplayNameAttr)> SetUpHrContributorAsync()
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

        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", SharedEmployeeId);
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrDescriptionAttr.Id, Attribute = hrDescriptionAttr, StringValue = HrDescription, ConnectedSystemObject = hrCso
        });

        return (hrSystem, hrImportRule, mvType, mvDescriptionAttr, mvDisplayNameAttr);
    }

    protected sealed record ExportTarget(
        ConnectedSystem System,
        ConnectedSystemObjectTypeAttribute DescriptionAttribute,
        ConnectedSystemObjectTypeAttribute DisplayNameAttribute);

    protected async Task<ExportTarget> AddExportTargetAsync(
        MetaverseObjectType mvType, MetaverseAttribute mvDisplayNameAttr, MetaverseAttribute mvDescriptionAttr,
        bool scopedOnDescription = false,
        OutboundDeprovisionAction scopeExitAction = OutboundDeprovisionAction.Delete)
    {
        var targetSystem = await CreateConnectedSystemAsync("AD Target");
        var targetExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var targetDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var targetDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "Description", Type = AttributeDataType.Text, Selected = true };
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "TargetUser",
            new List<ConnectedSystemObjectTypeAttribute> { targetExternalIdAttr, targetDisplayNameAttr, targetDescriptionAttr });

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
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvDisplayNameAttr, MetaverseAttributeId = mvDisplayNameAttr.Id } }
        });
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDescriptionAttr,
            TargetConnectedSystemAttributeId = targetDescriptionAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvDescriptionAttr, MetaverseAttributeId = mvDescriptionAttr.Id } }
        });

        if (scopedOnDescription)
        {
            // Only objects whose Description is the HR value are in scope, so withdrawing Description takes the
            // object out of scope; the rule deletes what leaves it.
            exportRule.OutboundDeprovisionAction = scopeExitAction;
            exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
            {
                Type = JIM.Models.Search.SearchGroupType.All,
                Criteria = new List<SyncRuleScopingCriteria>
                {
                    new()
                    {
                        MetaverseAttribute = mvDescriptionAttr,
                        ComparisonType = JIM.Models.Search.SearchComparisonType.Equals,
                        StringValue = HrDescription,
                        CaseSensitive = true
                    }
                }
            });
        }

        DbContext.SyncRules.Add(exportRule);
        await DbContext.SaveChangesAsync();
        SyncRepo.SeedSyncRule(exportRule);

        return new ExportTarget(targetSystem, targetDescriptionAttr, targetDisplayNameAttr);
    }

    /// <summary>
    /// Simulates the provisioning export having been executed against the target Connected System: marks the
    /// provisioned target CSO Normal, writes the exported values onto it, and clears all Pending Exports so
    /// assertions only see exports staged by the deprovisioning run under test.
    /// </summary>
    protected ConnectedSystemObject SimulateTargetExportExecuted(DeprovisioningContext ctx, string displayName, string description)
    {
        var targetCso = SyncRepo.ConnectedSystemObjects.Values.First(c => c.ConnectedSystemId == ctx.Target.Id);
        targetCso.Status = ConnectedSystemObjectStatus.Normal;
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = ctx.TargetDisplayNameAttribute.Id,
            Attribute = ctx.TargetDisplayNameAttribute,
            StringValue = displayName,
            ConnectedSystemObject = targetCso
        });
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = ctx.TargetDescriptionAttribute.Id,
            Attribute = ctx.TargetDescriptionAttribute,
            StringValue = description,
            ConnectedSystemObject = targetCso
        });
        SyncRepo.ClearAllPendingExports();
        return targetCso;
    }

    /// <summary>
    /// Marks every provisioned CSO of the target system Normal and clears all Pending Exports, simulating
    /// their provisioning exports having executed.
    /// </summary>
    protected void SimulateAllTargetExportsExecuted(ConnectedSystem targetSystem)
    {
        foreach (var targetCso in SyncRepo.ConnectedSystemObjects.Values.Where(c => c.ConnectedSystemId == targetSystem.Id))
            targetCso.Status = ConnectedSystemObjectStatus.Normal;
        SyncRepo.ClearAllPendingExports();
    }

    protected static SyncRuleMapping BuildDirectImportMapping(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source, int priority = int.MaxValue)
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

    protected async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new JIM.Application.Servers.SyncEngine(), new JIM.Application.Servers.SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }
}
