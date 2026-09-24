// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Workflow tests for the #242 Phase 2 work package H review fix: a re-election or recall that touches a
/// generated mapping must neither corrupt data nor fail an otherwise-legitimate operation.
/// <para>
/// Part A (export-mode markers, recall/deprovisioning paths): <see cref="ConnectedSystemServer.SyncRuleDeletionRecall"/>
/// and <see cref="ConnectedSystemServer.SynchronisedDeprovisioning"/> stage Pending Exports outside a run-scoped
/// Unique Value Generation context, so a generated export mapping's marker (staged on ANY relevant change,
/// since an expression-based mapping is conservatively relevant to every attribute change) must be stripped
/// before persistence, not thrown on: the earlier revision's throwing guard would have failed an ordinary
/// Synchronisation Rule deletion or Connected System deprovisioning outright.
/// </para>
/// <para>
/// Part B (import-mode re-election, five call sites of <see cref="ContributorReElectionService.ReElectSurvivingContributorsAsync"/>):
/// a survivor re-elected during a worker-driven obsoletion or out-of-scope disconnection can itself carry a
/// generated mapping. The worker's own re-election paths hold a run-scoped Unique Value Generation context and
/// must resolve it inline (value generated, assignment committed, no leftover marker reaching the page's
/// persistence integrity guard); the Synchronisation Rule deletion recall path holds no such context and must
/// clear the marker instead, leaving generation for the winning Connected System's own next synchronisation.
/// </para>
/// </summary>
[TestFixture]
public class GeneratedValueRecallAndReElectionWorkflowTests : WorkflowTestBase
{
    private const string SharedEmployeeId = "EMP001";

    #region Part A: export-mode marker stripping (recall / deprovisioning)

    [Test]
    public async Task ExecuteSyncRuleDeletionRecallAsync_TargetExportRuleHasGeneratedMapping_StripsMarkerKeepsOtherChangesAsync()
    {
        var ctx = await SetUpSoleContributorWithGeneratedExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        var ticketingCso = SimulateTargetProvisionedAsync(ctx);

        var assignmentIdBefore = SyncRepo.GeneratedValueAssignments.Keys.Single();
        var loginNameBefore = ticketingCso.AttributeValues.Single(av => av.AttributeId == ctx.TicketingLoginNameAttribute.Id).StringValue;

        var (task, activity) = await DisableRuleAndBuildTaskAsync(ctx.HrImportRule);
        var recallResult = await Jim.ConnectedSystems.ExecuteSyncRuleDeletionRecallAsync(task);

        var stagedPendingExport = SyncRepo.PendingExports.Values.SingleOrDefault(pe => pe.ConnectedSystemObjectId == ticketingCso.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await DbContext.SyncRules.FindAsync(ctx.HrImportRule.Id), Is.Null,
                "the recall must still complete and delete the rule, despite the target export rule carrying a generated mapping");
            Assert.That(recallResult.PendingExportsStaged, Is.GreaterThanOrEqualTo(1), "the non-generated DisplayName change must still be staged");

            Assert.That(stagedPendingExport, Is.Not.Null);
            Assert.That(stagedPendingExport!.AttributeValueChanges.Any(c => c.AttributeId == ctx.TicketingDisplayNameAttribute.Id),
                Is.True, "the ordinary DisplayName clear must still be staged");
            Assert.That(stagedPendingExport.AttributeValueChanges.Any(c => c.AttributeId == ctx.TicketingLoginNameAttribute.Id),
                Is.False, "nothing must be staged for the generated loginName attribute: its marker was never resolved here");
            Assert.That(stagedPendingExport.AttributeValueChanges.Any(c => c.PendingGeneration != null), Is.False,
                "no unresolved generation marker may reach persistence");

            Assert.That(ticketingCso.AttributeValues.Single(av => av.AttributeId == ctx.TicketingLoginNameAttribute.Id).StringValue,
                Is.EqualTo(loginNameBefore), "the Connected System Object's own generated value must be left untouched");
            Assert.That(SyncRepo.GeneratedValueAssignments.Keys.Single(), Is.EqualTo(assignmentIdBefore),
                "the existing GeneratedValueAssignment must be left untouched (no new assignment, none deleted)");
        }
    }

    [Test]
    public async Task ExecuteSynchronisedDeprovisioningAsync_TargetExportRuleHasGeneratedMapping_StripsMarkerKeepsOtherChangesAsync()
    {
        var ctx = await SetUpSoleContributorWithGeneratedExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        var ticketingCso = SimulateTargetProvisionedAsync(ctx);

        var assignmentIdBefore = SyncRepo.GeneratedValueAssignments.Keys.Single();
        var loginNameBefore = ticketingCso.AttributeValues.Single(av => av.AttributeId == ctx.TicketingLoginNameAttribute.Id).StringValue;

        var (task, activity) = await FenceSystemAndBuildTaskAsync(ctx.Hr);
        var result = await Jim.ConnectedSystems.ExecuteSynchronisedDeprovisioningAsync(task);

        var stagedPendingExport = SyncRepo.PendingExports.Values.SingleOrDefault(pe => pe.ConnectedSystemObjectId == ticketingCso.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await DbContext.ConnectedSystems.FindAsync(ctx.Hr.Id), Is.Null,
                "deprovisioning must still complete and delete the system, despite the target export rule carrying a generated mapping");
            Assert.That(result.PendingExportsStaged, Is.GreaterThanOrEqualTo(1));

            Assert.That(stagedPendingExport, Is.Not.Null);
            Assert.That(stagedPendingExport!.AttributeValueChanges.Any(c => c.AttributeId == ctx.TicketingDisplayNameAttribute.Id),
                Is.True, "the ordinary DisplayName clear must still be staged");
            Assert.That(stagedPendingExport.AttributeValueChanges.Any(c => c.AttributeId == ctx.TicketingLoginNameAttribute.Id),
                Is.False, "nothing must be staged for the generated loginName attribute: its marker was never resolved here");
            Assert.That(stagedPendingExport.AttributeValueChanges.Any(c => c.PendingGeneration != null), Is.False,
                "no unresolved generation marker may reach persistence");

            Assert.That(ticketingCso.AttributeValues.Single(av => av.AttributeId == ctx.TicketingLoginNameAttribute.Id).StringValue,
                Is.EqualTo(loginNameBefore), "the Connected System Object's own generated value must be left untouched");
            Assert.That(SyncRepo.GeneratedValueAssignments.Keys.Single(), Is.EqualTo(assignmentIdBefore),
                "the existing GeneratedValueAssignment must be left untouched (no new assignment, none deleted)");
        }
    }

    #endregion

    #region Part B: import-mode re-election

    [Test]
    public async Task ScopeExit_NextContributorIsGeneratedMapping_GeneratesValueAndCommitsAssignmentAsync()
    {
        var ctx = await SetUpHrAndGeneratedTrainingFallbackAsync(scoped: true);

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(GetAccountName(mvo, ctx), Is.EqualTo("jsmith"), "precondition: HR (priority 1) wins Account Name while in scope");
        Assert.That(SyncRepo.GeneratedValueAssignments, Is.Empty, "precondition: Training's mapping never won, so nothing was ever generated");

        PushHrOutOfScope(ctx);
        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
                Is.False, "the re-elected generated mapping must be resolved inline, never reaching the page's persistence guard");

            mvo = SyncRepo.MetaverseObjects[mvo.Id];
            Assert.That(mvo.PendingGeneratedValues, Is.Empty, "no marker may survive the page");
            Assert.That(GetAccountName(mvo, ctx), Is.EqualTo("emp001"),
                "Training's generated mapping (base Lower(cs[\"EmployeeId\"])) must have generated and been applied");

            Assert.That(SyncRepo.GeneratedValueAssignments, Has.Count.EqualTo(1));
            var assignment = SyncRepo.GeneratedValueAssignments.Values.Single();
            Assert.That(assignment.MetaverseObjectId, Is.EqualTo(mvo.Id));
            Assert.That(assignment.MetaverseAttributeId, Is.EqualTo(ctx.MvAccountNameAttributeId));
            Assert.That(assignment.Value, Is.EqualTo("emp001"));

            // Note: unlike an ordinary import-time generation (ProcessMetaverseObjectChangesAsync), the
            // out-of-scope disconnection path builds no RPEI outcome tree of its own to attach a
            // GeneratedValueAssigned child to; the value and the committed assignment are what this fix
            // guarantees. Wiring that RPEI child outcome in is a follow-up observability improvement, not a
            // correctness requirement (the assignment above is the durable record of what happened).
        }
    }

    [Test]
    public async Task Obsoletion_NextContributorIsGeneratedMapping_GeneratesValueAndCommitsAssignmentAsync()
    {
        var ctx = await SetUpHrAndGeneratedTrainingFallbackAsync(scoped: false);

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(GetAccountName(mvo, ctx), Is.EqualTo("jsmith"), "precondition: HR (priority 1) wins Account Name while joined");

        MarkCsoObsolete(ctx.HrCso);
        var activity = await RunDeltaSyncReturningActivityAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
                Is.False, "the re-elected generated mapping must be resolved inline, never reaching the page's persistence guard");

            mvo = SyncRepo.MetaverseObjects[mvo.Id];
            Assert.That(mvo.PendingGeneratedValues, Is.Empty, "no marker may survive the page");
            Assert.That(GetAccountName(mvo, ctx), Is.EqualTo("emp001"),
                "Training's generated mapping (base Lower(cs[\"EmployeeId\"])) must have generated and been applied");

            Assert.That(SyncRepo.GeneratedValueAssignments, Has.Count.EqualTo(1));
            var assignment = SyncRepo.GeneratedValueAssignments.Values.Single();
            Assert.That(assignment.MetaverseObjectId, Is.EqualTo(mvo.Id));
            Assert.That(assignment.MetaverseAttributeId, Is.EqualTo(ctx.MvAccountNameAttributeId));
            Assert.That(assignment.Value, Is.EqualTo("emp001"));
        }
    }

    [Test]
    public async Task ExecuteSyncRuleDeletionRecallAsync_ReElectsGeneratedMapping_ClearsPendingAndGeneratesOnNextSyncAsync()
    {
        var ctx = await SetUpHrAndGeneratedTrainingFallbackAsync(scoped: false);

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(GetAccountName(mvo, ctx), Is.EqualTo("jsmith"), "precondition: HR (priority 1) wins Account Name while joined");

        var (task, activity) = await DisableRuleAndBuildTaskAsync(ctx.HrImportRule);
        var recallResult = await Jim.ConnectedSystems.ExecuteSyncRuleDeletionRecallAsync(task);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await DbContext.SyncRules.FindAsync(ctx.HrImportRule.Id), Is.Null, "the rule must still be deleted");

            mvo = SyncRepo.MetaverseObjects[mvo.Id];
            Assert.That(mvo.PendingGeneratedValues, Is.Empty,
                "the recall path has no run-scoped generation context, so the re-elected marker must be cleared, not resolved");
            Assert.That(GetAccountName(mvo, ctx), Is.Null,
                "nothing generates the value here; the attribute is left for the winning system's own next synchronisation");
            Assert.That(SyncRepo.GeneratedValueAssignments, Is.Empty, "no assignment may be committed by the recall itself");
        }

        // The generating system's own next synchronisation now sees its mapping as the sole (and so winning)
        // contributor, and generates the value exactly as an ordinary import-mode generation would.
        var secondActivity = await RunFullSyncReturningActivityAsync(ctx.Training);

        mvo = SyncRepo.MetaverseObjects[mvo.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondActivity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
                Is.False);
            Assert.That(GetAccountName(mvo, ctx), Is.EqualTo("emp001"),
                "Training's own next synchronisation must now generate the value");
            Assert.That(SyncRepo.GeneratedValueAssignments, Has.Count.EqualTo(1));
        }
    }

    #endregion

    #region Helpers: Part A (export-mode)

    private sealed record ExportGenerationRecallContext(
        ConnectedSystem Hr,
        SyncRule HrImportRule,
        ConnectedSystem Ticketing,
        ConnectedSystemObjectTypeAttribute TicketingDisplayNameAttribute,
        ConnectedSystemObjectTypeAttribute TicketingLoginNameAttribute);

    /// <summary>
    /// HR is the sole contributor of DisplayName and EmployeeId (direct mappings). Ticketing's export rule
    /// provisions and carries TWO mappings: an ordinary DisplayName mapping, and a generated only-if-taken
    /// loginName mapping (base <c>Lower(mv["EmployeeId"])</c>). Recalling HR's contribution clears both MVO
    /// attributes, which makes the export rule "relevant" (an expression-based mapping is conservatively
    /// relevant to any change), so the generated mapping stages a marker too, right alongside the ordinary
    /// DisplayName clear.
    /// </summary>
    private async Task<ExportGenerationRecallContext> SetUpSoleContributorWithGeneratedExportTargetAsync()
    {
        var hr = await CreateConnectedSystemAsync("HR Source");
        var hrExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var hrDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var hrEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var hrType = await CreateCsoTypeAsync(hr.Id, "HrUser",
            new List<ConnectedSystemObjectTypeAttribute> { hrExternalIdAttr, hrDisplayNameAttr, hrEmployeeIdAttr });
        hrType.RemoveContributedAttributesOnObsoletion = true;

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var hrImportRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        hrImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImportRule,
            SyncRuleId = hrImportRule.Id,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrDisplayNameAttr, ConnectedSystemAttributeId = hrDisplayNameAttr.Id } }
        });
        hrImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImportRule,
            SyncRuleId = hrImportRule.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrEmployeeIdAttr, ConnectedSystemAttributeId = hrEmployeeIdAttr.Id } }
        });
        await DbContext.SaveChangesAsync();

        var ticketing = await CreateConnectedSystemAsync("Ticketing");
        var ticketingType = await CreateCsoTypeAsync(ticketing.Id, "TicketingUser", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "loginName", Type = AttributeDataType.Text, Selected = true }
        });
        var ticketingDisplayNameAttr = ticketingType.Attributes.Single(a => a.Name == "DisplayName");
        var ticketingLoginNameAttr = ticketingType.Attributes.Single(a => a.Name == "loginName");

        var exportRule = new SyncRule
        {
            ConnectedSystemId = ticketing.Id,
            Name = "Ticketing Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = ticketingType.Id,
            ConnectedSystemObjectType = ticketingType,
            MetaverseObjectTypeId = mvType.Id,
            MetaverseObjectType = mvType,
            ProvisionToConnectedSystem = true
        };
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = ticketingDisplayNameAttr,
            TargetConnectedSystemAttributeId = ticketingDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvDisplayNameAttr, MetaverseAttributeId = mvDisplayNameAttr.Id } }
        });
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = ticketingLoginNameAttr,
            TargetConnectedSystemAttributeId = ticketingLoginNameAttr.Id,
            Generation = new SyncRuleMappingGeneration
            {
                TokenKind = GeneratedValueTokenKind.OnlyIfTaken,
                SuffixStyle = GeneratedValueSuffixStyle.Number,
                SuffixStart = 1,
                AttemptLimit = 1000,
                NeverReuse = true
            },
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = "Lower(mv[\"EmployeeId\"])" } }
        });
        DbContext.SyncRules.Add(exportRule);
        await DbContext.SaveChangesAsync();
        SyncRepo.SeedSyncRule(exportRule);

        var hrCso = await CreateCsoAsync(hr.Id, hrType, "John Smith", SharedEmployeeId);
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrDisplayNameAttr.Id, Attribute = hrDisplayNameAttr, StringValue = "John Smith", ConnectedSystemObject = hrCso
        });

        return new ExportGenerationRecallContext(hr, hrImportRule, ticketing, ticketingDisplayNameAttr, ticketingLoginNameAttr);
    }

    /// <summary>
    /// Simulates the provisioning export having executed against Ticketing: marks the provisioned CSO Normal,
    /// writes back the exported DisplayName and the already-generated loginName, and clears Pending Exports so
    /// later assertions only see what the recall/deprovisioning under test stages.
    /// </summary>
    private ConnectedSystemObject SimulateTargetProvisionedAsync(ExportGenerationRecallContext ctx)
    {
        var ticketingCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Ticketing.Id);
        var loginNameChange = SyncRepo.PendingExports.Values
            .Single(pe => pe.ConnectedSystemObjectId == ticketingCso.Id)
            .AttributeValueChanges.Single(c => c.AttributeId == ctx.TicketingLoginNameAttribute.Id);
        Assert.That(loginNameChange.PendingGeneration, Is.Null, "precondition: provisioning-time generation must already be resolved");

        ticketingCso.Status = ConnectedSystemObjectStatus.Normal;
        ticketingCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = ctx.TicketingDisplayNameAttribute.Id, Attribute = ctx.TicketingDisplayNameAttribute,
            StringValue = "John Smith", ConnectedSystemObject = ticketingCso
        });
        ticketingCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = ctx.TicketingLoginNameAttribute.Id, Attribute = ctx.TicketingLoginNameAttribute,
            StringValue = loginNameChange.StringValue, ConnectedSystemObject = ticketingCso
        });
        SyncRepo.ClearAllPendingExports();
        return ticketingCso;
    }

    private async Task<(DeleteSyncRuleWorkerTask Task, Activity Activity)> DisableRuleAndBuildTaskAsync(SyncRule rule)
    {
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Modified).ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        rule.Enabled = false;
        rule.DisabledReason = "Deletion in progress: contributed attribute values are being recalled.";
        DbContext.Entry(rule).State = Microsoft.EntityFrameworkCore.EntityState.Modified;
        await DbContext.SaveChangesAsync();

        var activity = new Activity
        {
            TargetName = rule.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            TargetOperationType = ActivityTargetOperationType.RecallAttributeValues,
            Status = ActivityStatus.InProgress,
            ConnectedSystemId = rule.ConnectedSystemId,
            Executed = DateTime.UtcNow
        };
        DbContext.Activities.Add(activity);
        await DbContext.SaveChangesAsync();

        var task = new DeleteSyncRuleWorkerTask(rule.Id, recallContributedValues: true)
        {
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = Guid.NewGuid(),
            InitiatedByName = "Test Administrator",
            Activity = activity
        };
        return (task, activity);
    }

    private async Task<(DeleteConnectedSystemWorkerTask Task, Activity Activity)> FenceSystemAndBuildTaskAsync(ConnectedSystem system)
    {
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Modified).ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        var persistedSystem = await DbContext.ConnectedSystems.FindAsync(system.Id);
        persistedSystem!.Status = ConnectedSystemStatus.Deleting;
        await DbContext.SaveChangesAsync();

        var activity = new Activity
        {
            TargetName = system.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Deprovision,
            Status = ActivityStatus.InProgress,
            Executed = DateTime.UtcNow
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

    #endregion

    #region Helpers: Part B (import-mode)

    private sealed record GeneratedFallbackContext(
        ConnectedSystem Hr,
        ConnectedSystem Training,
        ConnectedSystemObject HrCso,
        ConnectedSystemObjectTypeAttribute? HrScopeFlagAttribute,
        MetaverseObjectType MvType,
        int MvAccountNameAttributeId,
        SyncRule HrImportRule);

    /// <summary>
    /// HR (priority 1, direct Account Name and Employee Id mappings, projects) and Training (priority 2, joins
    /// on Employee Id, GENERATED Account Name mapping based on <c>Lower(cs["EmployeeId"])</c>, i.e. Training's
    /// OWN Connected System Object's Employee Id, never the Metaverse's). While HR is present it wins Account
    /// Name outright: Training's generated mapping loses the priority gate before its base expression is even
    /// evaluated, so nothing is ever generated. Training becomes the winning contributor only once HR withdraws
    /// (obsoletion, scope exit, or its Synchronisation Rule being deleted). Basing the generated mapping on
    /// Training's own Connected System Object (rather than the Metaverse's Employee Id, which HR alone
    /// contributes and which is cleared along with HR's withdrawal) means Training can still generate correctly
    /// after HR is completely gone, exactly as the "recall re-elects a generated import mapping" scenario needs.
    /// </summary>
    private async Task<GeneratedFallbackContext> SetUpHrAndGeneratedTrainingFallbackAsync(bool scoped)
    {
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var hrExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var hrEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var hrAccountNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "HrAccountName", Type = AttributeDataType.Text, Selected = true };
        var hrScopeFlagAttr = scoped ? new ConnectedSystemObjectTypeAttribute { Name = "ScopeFlag", Type = AttributeDataType.Text, Selected = true } : null;
        var hrAttributes = new List<ConnectedSystemObjectTypeAttribute> { hrExternalIdAttr, hrEmployeeIdAttr, hrAccountNameAttr };
        if (hrScopeFlagAttr != null)
            hrAttributes.Add(hrScopeFlagAttr);
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser", hrAttributes);
        hrType.RemoveContributedAttributesOnObsoletion = true;

        var trainingSystem = await CreateConnectedSystemAsync("Training Source");
        trainingSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
        var trainingExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var trainingEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var trainingType = await CreateCsoTypeAsync(trainingSystem.Id, "TrainingRecord",
            new List<ConnectedSystemObjectTypeAttribute> { trainingExternalIdAttr, trainingEmployeeIdAttr });
        trainingType.RemoveContributedAttributesOnObsoletion = true;

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvAccountNameAttr = new MetaverseAttribute
        {
            Name = "AccountName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvAccountNameAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvAccountNameAttr);

        var hrImportRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        hrImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImportRule,
            SyncRuleId = hrImportRule.Id,
            Priority = 1,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrEmployeeIdAttr, ConnectedSystemAttributeId = hrEmployeeIdAttr.Id } }
        });
        hrImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImportRule,
            SyncRuleId = hrImportRule.Id,
            Priority = 1,
            TargetMetaverseAttribute = mvAccountNameAttr,
            TargetMetaverseAttributeId = mvAccountNameAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrAccountNameAttr, ConnectedSystemAttributeId = hrAccountNameAttr.Id } }
        });
        if (scoped)
        {
            hrImportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
            {
                Type = SearchGroupType.All,
                Criteria = new List<SyncRuleScopingCriteria>
                {
                    new()
                    {
                        ConnectedSystemAttribute = hrScopeFlagAttr,
                        ComparisonType = SearchComparisonType.Equals,
                        StringValue = "InScope",
                        CaseSensitive = true
                    }
                }
            });
        }
        await DbContext.SaveChangesAsync();

        var trainingImportRule = await CreateImportSyncRuleAsync(trainingSystem.Id, trainingType, mvType, "Training Import", enableProjection: false);
        trainingImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = trainingImportRule,
            SyncRuleId = trainingImportRule.Id,
            Priority = 2,
            TargetMetaverseAttribute = mvAccountNameAttr,
            TargetMetaverseAttributeId = mvAccountNameAttr.Id,
            Generation = new SyncRuleMappingGeneration
            {
                TokenKind = GeneratedValueTokenKind.OnlyIfTaken,
                SuffixStyle = GeneratedValueSuffixStyle.Number,
                SuffixStart = 1,
                AttemptLimit = 1000,
                NeverReuse = true
            },
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = "Lower(cs[\"EmployeeId\"])" } }
        });
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

        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", SharedEmployeeId);
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrAccountNameAttr.Id, Attribute = hrAccountNameAttr, StringValue = "jsmith", ConnectedSystemObject = hrCso
        });
        if (hrScopeFlagAttr != null)
        {
            hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                AttributeId = hrScopeFlagAttr.Id, Attribute = hrScopeFlagAttr, StringValue = "InScope", ConnectedSystemObject = hrCso
            });
        }

        await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", SharedEmployeeId);

        return new GeneratedFallbackContext(hrSystem, trainingSystem, hrCso, hrScopeFlagAttr, mvType, mvAccountNameAttr.Id, hrImportRule);
    }

    private static string? GetAccountName(MetaverseObject mvo, GeneratedFallbackContext ctx) =>
        mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvAccountNameAttributeId && !av.NullValue)?.StringValue;

    /// <summary>
    /// Flips HR's ScopeFlag so it no longer satisfies its import rule's scoping criteria, then touches
    /// LastUpdated so the next Full Sync re-evaluates the CSO instead of skipping it as unchanged.
    /// </summary>
    private static void PushHrOutOfScope(GeneratedFallbackContext ctx)
    {
        var scopeFlagValue = ctx.HrCso.AttributeValues.Single(av => av.AttributeId == ctx.HrScopeFlagAttribute!.Id);
        scopeFlagValue.StringValue = "OutOfScope";
        ctx.HrCso.LastUpdated = DateTime.UtcNow;
    }

    private static void MarkCsoObsolete(ConnectedSystemObject cso)
    {
        cso.Status = ConnectedSystemObjectStatus.Obsolete;
        cso.LastUpdated = DateTime.UtcNow;
    }

    #endregion

    #region Helpers: shared

    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new JIM.Application.Servers.SyncEngine(), new JIM.Application.Servers.SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }

    private async Task<Activity> RunFullSyncReturningActivityAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new JIM.Application.Servers.SyncEngine(), new JIM.Application.Servers.SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    private async Task<Activity> RunDeltaSyncReturningActivityAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new JIM.Application.Servers.SyncEngine(), new JIM.Application.Servers.SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();
        return activity;
    }

    #endregion
}
