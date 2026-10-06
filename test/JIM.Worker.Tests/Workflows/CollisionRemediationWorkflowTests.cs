// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// End-to-end workflow tests for Collision Remediation (Unique Value Generation, #242, release 4; PRD Scenarios 5, 6
/// and 10), driving the real synchronisation and export pipelines against the in-memory repositories. Topology: an "HR"
/// Connected System whose import Synchronisation Rule projects a Person with a generated Account Name (base
/// <c>Lower(cs["first"]) + "." + Lower(cs["last"])</c>, only-if-taken) and a User Principal Name derived from it
/// (<c>mv["Account Name"] + "@corp.local"</c>, #1750); a "Directory" Connected System provisioned with
/// sAMAccountName and userPrincipalName, whose Connector classifies "value already in use" rejections; and, for the
/// anchoring cases, a "Contractor" Connected System that also receives the Account Name.
/// </summary>
[TestFixture]
public class CollisionRemediationWorkflowTests : WorkflowTestBase
{
    private const string AlreadyInUse = "00000524: SvcErr: DSID-031A1254, problem 5003 (WILL_NOT_PERFORM): the account name is already in use";

    #region Remediation, import mode (Scenario 5)

    [Test]
    public async Task Export_UnanchoredGeneratedValueRejected_RevisesTheMetaverseValueAndLeavesTheExportQueuedAsync()
    {
        var ctx = await SetUpAsync();
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        var exportBefore = DirectoryExport(ctx);

        var (activity, logEvents) = await RunExportCapturingLogsAsync(ctx.Directory, RejectFirst("sAMAccountName"));

        var assignment = SyncRepo.GeneratedValueAssignments.Values.Single();
        var item = ExecutionItems(activity).Single();
        var record = SyncRepo.GeneratedValueRevisionsPending.Values.Single();
        var exportAfter = SyncRepo.PendingExports[exportBefore.Id];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AccountName(ctx), Is.EqualTo("joe.bloggs1"), "the Metaverse value is revised to the next candidate");
            Assert.That(assignment.Value, Is.EqualTo("joe.bloggs1"));
            Assert.That(assignment.PreviousValue, Is.EqualTo("joe.bloggs"));
            Assert.That(assignment.State, Is.EqualTo(GeneratedValueAssignmentState.Remediated));
            Assert.That(assignment.RemediationCount, Is.EqualTo(1));
            Assert.That(assignment.RemediatedByActivityRunProfileExecutionItemId, Is.EqualTo(item.Id));

            Assert.That(record.MetaverseObjectId, Is.EqualTo(Mvo().Id));
            Assert.That(record.MetaverseAttributeId, Is.EqualTo(ctx.AccountName.Id));
            Assert.That(record.RemediatingActivityRunProfileExecutionItemId, Is.EqualTo(item.Id), "the drain's causal edge points back at this item");
            Assert.That(record.ReasonCode, Is.EqualTo(CausalReasonCode.GeneratedValueAlreadyInUse));

            Assert.That(exportAfter.Status, Is.EqualTo(PendingExportStatus.Pending), "left queued for the next synchronisation, not failed");
            Assert.That(exportAfter.ErrorCount, Is.Zero, "a remediation does not consume the export's error count");

            Assert.That(item.ErrorType is null or ActivityRunProfileExecutionItemErrorType.NotSet, Is.True, "a corrected value is not an error");
            Assert.That(item.SyncOutcomes.Select(o => o.OutcomeType), Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueRemediated));

            Assert.That(logEvents.Where(e => e.Level >= LogEventLevel.Error).Select(e => e.RenderMessage()), Is.Empty, "a handled rejection is logged below Error");
            Assert.That(logEvents.Any(e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("corrected")), Is.True);
        }
    }

    [Test]
    public async Task Export_UnanchoredGeneratedValueRejected_RecordsTheChangeWithProvenanceOnTheMetaverseObjectAsync()
    {
        var ctx = await SetUpAsync();
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);

        await RunExportAsync(ctx.Directory, RejectFirst("sAMAccountName"));

        var value = Mvo().AttributeValues.Single(av => av.AttributeId == ctx.AccountName.Id);
        var change = SyncRepo.MetaverseObjectChanges.Values.Single(c => c.InitiatedByName == MetaverseServer.CollisionRemediationInitiatorName);
        var attributeChange = change.AttributeChanges.Single(ac => ac.AttributeName == "Account Name");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.ContributedBySyncRuleId, Is.EqualTo(ctx.HrImportRuleId), "the corrected value is still the generated mapping's");
            Assert.That(attributeChange.ValueChanges.Single(v => v.ValueChangeType == ValueChangeType.Remove).StringValue, Is.EqualTo("joe.bloggs"));
            Assert.That(attributeChange.ValueChanges.Single(v => v.ValueChangeType == ValueChangeType.Add).StringValue, Is.EqualTo("joe.bloggs1"));
        }
    }

    [Test]
    public async Task Export_UnanchoredGeneratedValueRejected_RecordsTheCarryingAttributeSetFromTheRejectedValueAsync()
    {
        var ctx = await SetUpAsync();
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);

        var activity = await RunExportAsync(ctx.Directory, RejectFirst("sAMAccountName"));

        var change = ExecutionItems(activity).Single().ConnectedSystemObjectChange;
        Assert.That(change, Is.Not.Null);
        var row = change!.AttributeChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.ChangeType, Is.EqualTo(ObjectChangeType.PendingExport), "nothing was written to the Connected System");
            Assert.That(row.AttributeName, Is.EqualTo("sAMAccountName"));
            Assert.That(row.ValueChanges.Single(v => v.ValueChangeType == ValueChangeType.Remove).StringValue, Is.EqualTo("joe.bloggs"));
            Assert.That(row.ValueChanges.Single(v => v.ValueChangeType == ValueChangeType.Add).StringValue, Is.EqualTo("joe.bloggs1"));
        }
    }

    [Test]
    public async Task Export_UnanchoredGeneratedValueRejected_MarksTheDerivedValuesHostForReDerivationAsync()
    {
        var ctx = await SetUpAsync();
        var hrCso = await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);

        await RunExportAsync(ctx.Directory, RejectFirst("sAMAccountName"));

        Assert.That(SyncRepo.ConnectedSystemObjects[hrCso.Id].DerivedInputChangePending, Is.True,
            "HR hosts the User Principal Name derived from Account Name, so its next synchronisation re-derives it");
    }

    [Test]
    public async Task Export_RejectionNamesTheDerivedUserPrincipalName_RemediatesTheGeneratedAccountNameAsync()
    {
        var ctx = await SetUpAsync();
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);

        await RunExportAsync(ctx.Directory, RejectFirst("userPrincipalName"));

        Assert.That(AccountName(ctx), Is.EqualTo("joe.bloggs1"), "a User Principal Name derived from one generated value is that value's collision");
    }

    #endregion

    #region The revision-pending record and the next synchronisation

    [Test]
    public async Task DeltaSync_WithOnlyARevisionPending_UpdatesTheQueuedExportWritesTheEdgeAndDeletesTheRecordAsync()
    {
        var ctx = await SetUpAsync();
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        var exportActivity = await RunExportAsync(ctx.Directory, RejectFirst("sAMAccountName"));
        var remediatingItemId = ExecutionItems(exportActivity).Single().Id;

        // Nothing else is pending for Directory: its delta synchronisation must still drain the record.
        var syncActivity = await RunDeltaSyncAsync(ctx.Directory);

        var queued = DirectoryExports(ctx);
        var edge = SyncRepo.CausalEdges.SingleOrDefault(e => e.EdgeType == CausalEdgeType.ExportRejectionCausedGeneratedValueRevision);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queued, Has.Count.EqualTo(1), "the queued export is updated, not duplicated");
            var export = queued.Single();
            Assert.That(export.ChangeType, Is.EqualTo(PendingExportChangeType.Create), "the provisioning export stays a Create");
            Assert.That(export.AttributeValueChanges.Where(c => c.AttributeId == ctx.SAMAccountName.Id).Select(c => c.StringValue),
                Is.EqualTo(new[] { "joe.bloggs1" }), "the rejected value is replaced by the corrected one, exactly once");
            Assert.That(SyncRepo.GeneratedValueRevisionsPending, Is.Empty, "a drained record is deleted");
            Assert.That(edge, Is.Not.Null, "the queueing item says why its value changed");
            Assert.That(edge!.CauseRunProfileExecutionItemId, Is.EqualTo(remediatingItemId));
            Assert.That(edge.ReasonCode, Is.EqualTo(CausalReasonCode.GeneratedValueAlreadyInUse));
            Assert.That(ExecutionItems(syncActivity).Select(i => i.Id), Does.Contain(edge.EffectRunProfileExecutionItemId),
                "the edge lands on the synchronisation's item for the object");
        }
    }

    [Test]
    public async Task FullSync_OfAnotherSystem_AlsoDrainsTheRevisionAsync()
    {
        var ctx = await SetUpAsync();
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        await RunExportAsync(ctx.Directory, RejectFirst("sAMAccountName"));

        await RunFullSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.GeneratedValueRevisionsPending, Is.Empty);
            Assert.That(DirectoryExports(ctx).Single().AttributeValueChanges.Where(c => c.AttributeId == ctx.SAMAccountName.Id).Select(c => c.StringValue),
                Is.EqualTo(new[] { "joe.bloggs1" }));
            Assert.That(DirectoryExports(ctx).Single().AttributeValueChanges.Where(c => c.AttributeId == ctx.DirectoryUserPrincipalName.Id).Select(c => c.StringValue),
                Is.EqualTo(new[] { "joe.bloggs1@corp.local" }), "HR's re-derivation carries the corrected value into the derived attribute too");
        }
    }

    [Test]
    public async Task FullSync_DrainingARevisionOntoARejectedCreateCarryingAnExportModeGeneratedValue_KeepsThatValueAsync()
    {
        // Found by Scenario 023 against Samba AD: the rejected Create also carries an export-mode generated value. The
        // drain re-evaluates the object's exports, which re-stages that mapping as an unresolved generation marker; the
        // Create has already been attempted, so the change is appended onto it in place, a path with no deferred
        // resolution step, and the synchronisation failed outright rather than keep the value the Create already holds.
        var ctx = await SetUpAsync(directoryExportModeEmployeeNumber: true);
        var employeeNumber = DirectoryAttribute(ctx, "employeeNumber");
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        var generatedBefore = DirectoryExport(ctx).AttributeValueChanges.Single(c => c.AttributeId == employeeNumber.Id).StringValue;
        await RunExportAsync(ctx.Directory, RejectFirst("sAMAccountName"));

        var syncActivity = await RunFullSyncAsync(ctx.Hr);

        var export = DirectoryExport(ctx);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(syncActivity.Status, Is.Not.EqualTo(ActivityStatus.FailedWithError), syncActivity.ErrorMessage);
            Assert.That(SyncRepo.GeneratedValueRevisionsPending, Is.Empty, "the revision is drained");
            Assert.That(export.AttributeValueChanges.Where(c => c.AttributeId == ctx.SAMAccountName.Id).Select(c => c.StringValue),
                Is.EqualTo(new[] { "joe.bloggs1" }), "the corrected value reaches the queued Create");
            Assert.That(export.AttributeValueChanges.Where(c => c.AttributeId == employeeNumber.Id).Select(c => c.StringValue),
                Is.EqualTo(new[] { generatedBefore }), "the export-mode generated value the Create already carries is kept, exactly once");
            Assert.That(export.AttributeValueChanges.Any(c => c.PendingGeneration != null), Is.False, "no unresolved marker is left on the export");
        }
    }

    [Test]
    public async Task Export_RejectionOfAValueAlreadyCorrected_IsAnOrdinaryErrorAndDoesNotRemediateAgainAsync()
    {
        var ctx = await SetUpAsync();
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        await RunExportAsync(ctx.Directory, RejectFirst("sAMAccountName"));

        // Exported again before any synchronisation re-staged it: the export still carries the rejected value.
        var second = await RunExportAsync(ctx.Directory, RejectAll("sAMAccountName"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AccountName(ctx), Is.EqualTo("joe.bloggs1"), "no second value is skipped for a rejection JIM has already answered");
            Assert.That(ExecutionItems(second).Single().ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UniqueValueAlreadyInUse));
        }
    }

    #endregion

    #region Needs Decision (Scenario 6)

    [Test]
    public async Task Export_AnchoredValueRejected_EntersNeedsDecisionAndParksTheExportAsync()
    {
        var ctx = await SetUpAsync();
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        SeedContractorAccount(ctx, Mvo().Id, "joe.bloggs"); // Contractor has already accepted it
        var export = DirectoryExport(ctx);

        var activity = await RunExportAsync(ctx.Directory, RejectAll("sAMAccountName"));

        var assignment = SyncRepo.GeneratedValueAssignments.Values.Single();
        var item = ExecutionItems(activity).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AccountName(ctx), Is.EqualTo("joe.bloggs"), "an anchored value is never revised automatically");
            Assert.That(assignment.State, Is.EqualTo(GeneratedValueAssignmentState.NeedsDecision));
            Assert.That(assignment.RejectedByConnectedSystemId, Is.EqualTo(ctx.Directory.Id));
            Assert.That(assignment.AnchoredByConnectedSystemId, Is.EqualTo(ctx.Contractor.Id));
            Assert.That(assignment.NeedsDecisionActivityRunProfileExecutionItemId, Is.EqualTo(item.Id));
            Assert.That(SyncRepo.PendingExports[export.Id].Status, Is.EqualTo(PendingExportStatus.Parked));
            Assert.That(SyncRepo.PendingExports[export.Id].ErrorCount, Is.Zero);
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.GeneratedValueCollisionUnresolved));
            Assert.That(item.ErrorMessage, Does.Contain("Directory").And.Contain("joe.bloggs").And.Contain("Contractor"),
                "the error names the rejecting system, the value and the system that has provisioned it");
            Assert.That(SyncRepo.GeneratedValueRevisionsPending, Is.Empty);
        }
    }

    [Test]
    public async Task Export_ParticipatingSystemClearedAndNotReimported_CannotTellSoEntersNeedsDecisionAsync()
    {
        var ctx = await SetUpAsync();
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        var contractor = SyncRepo.ConnectedSystems[ctx.Contractor.Id];
        contractor.StrandedValueSweepArmedAt = DateTime.UtcNow;
        contractor.LastSuccessfulFullImportCompletedAt = null;

        var activity = await RunExportAsync(ctx.Directory, RejectAll("sAMAccountName"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AccountName(ctx), Is.EqualTo("joe.bloggs"), "missing knowledge never permits a rename");
            Assert.That(SyncRepo.GeneratedValueAssignments.Values.Single().State, Is.EqualTo(GeneratedValueAssignmentState.NeedsDecision));
            Assert.That(ExecutionItems(activity).Single().ErrorMessage, Does.Contain("cannot tell"));
        }
    }

    [Test]
    public async Task Export_RemediationExhausted_EntersNeedsDecisionAsync()
    {
        var ctx = await SetUpAsync();
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        SyncRepo.GeneratedValueAssignments.Values.Single().RemediationCount = JIM.Application.UniqueValues.UniqueValueGenerationServer.MaximumRemediations;

        await RunExportAsync(ctx.Directory, RejectAll("sAMAccountName"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AccountName(ctx), Is.EqualTo("joe.bloggs"));
            Assert.That(SyncRepo.GeneratedValueAssignments.Values.Single().State, Is.EqualTo(GeneratedValueAssignmentState.NeedsDecision));
        }
    }

    [Test]
    public async Task Export_AfterTheRenameIsAuthorised_RemediatesTheAnchoredValueAsync()
    {
        var ctx = await SetUpAsync();
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        SeedContractorAccount(ctx, Mvo().Id, "joe.bloggs");
        await RunExportAsync(ctx.Directory, RejectAll("sAMAccountName"));
        var assignmentId = SyncRepo.GeneratedValueAssignments.Keys.Single();

        var authorised = await Jim.UniqueValues.AuthoriseRenameAsync(assignmentId, "Ada Admin");
        await RunExportAsync(ctx.Directory, RejectAll("sAMAccountName"));

        var assignment = SyncRepo.GeneratedValueAssignments[assignmentId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(authorised, Is.True);
            Assert.That(AccountName(ctx), Is.EqualTo("joe.bloggs1"), "the authorised rename is performed at the next rejection");
            Assert.That(assignment.State, Is.EqualTo(GeneratedValueAssignmentState.Remediated));
            Assert.That(assignment.RenameAuthorised, Is.False, "an authorisation covers one rename");
            Assert.That(assignment.RenameAuthorisedByName, Is.EqualTo("Ada Admin"));
            Assert.That(SyncRepo.GeneratedValueRevisionsPending.Values.Single().ReasonCode, Is.EqualTo(CausalReasonCode.GeneratedValueRenameAuthorised));
        }
    }

    [Test]
    public async Task RetryFailedExports_ParkedExport_IsLeftParkedAsync()
    {
        var ctx = await SetUpAsync();
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        SeedContractorAccount(ctx, Mvo().Id, "joe.bloggs");
        await RunExportAsync(ctx.Directory, RejectAll("sAMAccountName"));
        var export = DirectoryExport(ctx);

        await Jim.ExportExecution.RetryFailedExportsAsync(ctx.Directory.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.PendingExports[export.Id].Status, Is.EqualTo(PendingExportStatus.Parked),
                "the bulk retry is for failed exports; a parked one waits on its decision");
            Assert.That(await SyncRepo.GetExecutableExportCountAsync(ctx.Directory.Id), Is.Zero, "a parked export is never exported");
        }
    }

    [Test]
    public async Task Retry_NeedsDecision_TheNextExportTriesTheSameValueAgainAsync()
    {
        var ctx = await SetUpAsync();
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        SeedContractorAccount(ctx, Mvo().Id, "joe.bloggs");
        await RunExportAsync(ctx.Directory, RejectAll("sAMAccountName"));
        var assignmentId = SyncRepo.GeneratedValueAssignments.Keys.Single();
        var connector = new MockCallConnector().WithConnectedSystemExportResultFactory(_ => ConnectedSystemExportResult.Succeeded());

        var released = await Jim.UniqueValues.RetryAsync(assignmentId);
        await RunExportAsync(ctx.Directory, connector);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.True);
            Assert.That(connector.ExportedItems.SelectMany(pe => pe.AttributeValueChanges).Where(c => c.AttributeId == ctx.SAMAccountName.Id).Select(c => c.StringValue),
                Is.EqualTo(new[] { "joe.bloggs" }), "the conflict was fixed at its source, so the same value goes out again");
            Assert.That(SyncRepo.GeneratedValueAssignments[assignmentId].State, Is.EqualTo(GeneratedValueAssignmentState.Committed));
        }
    }

    #endregion

    #region Ordinary export errors (Scenario 10)

    [Test]
    public async Task Export_CollisionRemediationSwitchedOff_IsAnOrdinaryErrorAndLeavesTheAssignmentAsync()
    {
        var ctx = await SetUpAsync();
        GeneratedMapping(ctx).Generation!.CollisionRemediation = false;
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        var export = DirectoryExport(ctx);

        var activity = await RunExportAsync(ctx.Directory, RejectAll("sAMAccountName"));

        AssertOrdinaryError(ctx, activity, export);
    }

    [Test]
    public async Task Export_ConnectorDoesNotClassifyRejections_IsAnOrdinaryErrorAsync()
    {
        var ctx = await SetUpAsync(connectorClassifies: false);
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        var export = DirectoryExport(ctx);

        var activity = await RunExportAsync(ctx.Directory, RejectAll("sAMAccountName"));

        AssertOrdinaryError(ctx, activity, export);
    }

    [Test]
    public async Task Export_RejectionNamesAnAttributeJimDoesNotRecognise_IsAnOrdinaryErrorAsync()
    {
        var ctx = await SetUpAsync();
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        var export = DirectoryExport(ctx);

        var activity = await RunExportAsync(ctx.Directory, RejectAll("msDS-SomethingElse"));

        AssertOrdinaryError(ctx, activity, export);
    }

    private void AssertOrdinaryError(Context ctx, Activity activity, PendingExport export)
    {
        var item = ExecutionItems(activity).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AccountName(ctx), Is.EqualTo("joe.bloggs"));
            Assert.That(SyncRepo.GeneratedValueAssignments.Values.Single().State, Is.EqualTo(GeneratedValueAssignmentState.Committed), "the assignment is unchanged");
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UniqueValueAlreadyInUse), "the classified error from PR 8a");
            Assert.That(SyncRepo.PendingExports[export.Id].ErrorCount, Is.EqualTo(1), "an ordinary export error counts towards retries");
            Assert.That(SyncRepo.GeneratedValueRevisionsPending, Is.Empty);
        }
    }

    #endregion

    #region Export mode (Scenario 9)

    [Test]
    public async Task Export_ExportModeGeneratedValueRejected_RewritesTheQueuedExportAsync()
    {
        var ctx = await SetUpAsync();
        var (ticketing, loginName) = await AddTicketingExportModeLoginNameAsync(ctx);
        await SeedHrPersonAsync(ctx, "Joe", "Bloggs", "E1");
        await RunFullSyncAsync(ctx.Hr);
        var ticketingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemId == ticketing.Id);

        var activity = await RunExportAsync(ticketing, RejectFirst("loginName"));

        var assignment = SyncRepo.GeneratedValueAssignments.Values.Single(a => a.ConnectedSystemObjectId.HasValue);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.PendingExports[ticketingExport.Id].AttributeValueChanges.Single(c => c.AttributeId == loginName.Id).StringValue,
                Is.EqualTo("e11"), "an export-mode value is corrected on the queued export itself");
            Assert.That(SyncRepo.PendingExports[ticketingExport.Id].Status, Is.EqualTo(PendingExportStatus.Pending));
            Assert.That(assignment.Value, Is.EqualTo("e11"));
            Assert.That(assignment.State, Is.EqualTo(GeneratedValueAssignmentState.Remediated));
            Assert.That(SyncRepo.GeneratedValueRevisionsPending, Is.Empty, "no Metaverse Object is involved");
            Assert.That(ExecutionItems(activity).Single().SyncOutcomes.Select(o => o.OutcomeType), Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueRemediated));
        }
    }

    #endregion

    #region Context and helpers

    private sealed record Context(
        ConnectedSystem Hr,
        ConnectedSystem Directory,
        ConnectedSystem Contractor,
        MetaverseObjectType MvType,
        MetaverseAttribute AccountName,
        MetaverseAttribute UserPrincipalName,
        int HrCsoTypeId,
        int HrImportRuleId,
        ConnectedSystemObjectTypeAttribute SAMAccountName,
        ConnectedSystemObjectTypeAttribute DirectoryUserPrincipalName,
        ConnectedSystemObjectTypeAttribute ContractorUid,
        int ContractorCsoTypeId);

    private async Task<Context> SetUpAsync(bool connectorClassifies = true, bool directoryExportModeEmployeeNumber = false)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var employeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var accountName = await AddMetaverseAttributeAsync(mvType, "Account Name");
        var userPrincipalName = await AddMetaverseAttributeAsync(mvType, "User Principal Name");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "HrUser", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "first", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "last", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "employeeId", Type = AttributeDataType.Text, Selected = true }
        });
        var hrEmployeeId = hrType.Attributes.Single(a => a.Name == "employeeId");

        var importRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule, SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = employeeId, TargetMetaverseAttributeId = employeeId.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrEmployeeId, ConnectedSystemAttributeId = hrEmployeeId.Id } }
        });
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule, SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = accountName, TargetMetaverseAttributeId = accountName.Id,
            Generation = new SyncRuleMappingGeneration
            {
                TokenKind = GeneratedValueTokenKind.OnlyIfTaken, SuffixStyle = GeneratedValueSuffixStyle.Number, SuffixStart = 1,
                AttemptLimit = 1000, NeverReuse = true, CollisionRemediation = true
            },
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = "Lower(cs[\"first\"]) + \".\" + Lower(cs[\"last\"])" } }
        });
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule, SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = userPrincipalName, TargetMetaverseAttributeId = userPrincipalName.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = "mv[\"Account Name\"] + \"@corp.local\"" } }
        });

        var directory = await CreateConnectedSystemAsync("Directory");
        directory.ConnectorDefinition.SupportsUniquenessRejectionClassification = connectorClassifies;
        var directoryAttributes = new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "sAMAccountName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "userPrincipalName", Type = AttributeDataType.Text, Selected = true }
        };
        if (directoryExportModeEmployeeNumber)
            directoryAttributes.Add(new() { Name = "employeeNumber", Type = AttributeDataType.Text, Selected = true });
        var directoryType = await CreateCsoTypeAsync(directory.Id, "DirectoryUser", directoryAttributes);
        var sAMAccountName = directoryType.Attributes.Single(a => a.Name == "sAMAccountName");
        var directoryUpn = directoryType.Attributes.Single(a => a.Name == "userPrincipalName");
        var directoryExport = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Export", enableProvisioning: true);
        AddDirectFlow(directoryExport, sAMAccountName, accountName);
        AddDirectFlow(directoryExport, directoryUpn, userPrincipalName);
        if (directoryExportModeEmployeeNumber)
        {
            // An export-mode generated value on the same target as the import-mode Account Name.
            var employeeNumber = directoryType.Attributes.Single(a => a.Name == "employeeNumber");
            directoryExport.AttributeFlowRules.Add(new SyncRuleMapping
            {
                SyncRule = directoryExport, SyncRuleId = directoryExport.Id,
                TargetConnectedSystemAttribute = employeeNumber, TargetConnectedSystemAttributeId = employeeNumber.Id,
                Generation = new SyncRuleMappingGeneration
                {
                    TokenKind = GeneratedValueTokenKind.OnlyIfTaken, SuffixStyle = GeneratedValueSuffixStyle.Number, SuffixStart = 1,
                    AttemptLimit = 1000, NeverReuse = true, CollisionRemediation = true
                },
                Sources = { new SyncRuleMappingSource { Order = 0, Expression = "\"n-\" + Lower(mv[\"EmployeeId\"])" } }
            });
        }

        // Contractor receives the Account Name too (a participating target), but provisions nothing itself: the
        // anchoring cases seed its account directly.
        var contractor = await CreateConnectedSystemAsync("Contractor");
        var contractorType = await CreateCsoTypeAsync(contractor.Id, "ContractorUser", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "id", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "uid", Type = AttributeDataType.Text, Selected = true }
        });
        var contractorUid = contractorType.Attributes.Single(a => a.Name == "uid");
        var contractorExport = await CreateExportSyncRuleAsync(contractor.Id, contractorType, mvType, "Contractor Export", enableProvisioning: false);
        AddDirectFlow(contractorExport, contractorUid, accountName);

        await DbContext.SaveChangesAsync();
        return new Context(hr, directory, contractor, mvType, accountName, userPrincipalName, hrType.Id, importRule.Id, sAMAccountName, directoryUpn, contractorUid, contractorType.Id);
    }

    private async Task<(ConnectedSystem Ticketing, ConnectedSystemObjectTypeAttribute LoginName)> AddTicketingExportModeLoginNameAsync(Context ctx)
    {
        var ticketing = await CreateConnectedSystemAsync("Ticketing");
        ticketing.ConnectorDefinition.SupportsUniquenessRejectionClassification = true;
        var ticketingType = await CreateCsoTypeAsync(ticketing.Id, "TicketingUser", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "loginName", Type = AttributeDataType.Text, Selected = true }
        });
        var loginName = ticketingType.Attributes.Single(a => a.Name == "loginName");
        var exportRule = await CreateExportSyncRuleAsync(ticketing.Id, ticketingType, ctx.MvType, "Ticketing Export", enableProvisioning: true);
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule, SyncRuleId = exportRule.Id,
            TargetConnectedSystemAttribute = loginName, TargetConnectedSystemAttributeId = loginName.Id,
            Generation = new SyncRuleMappingGeneration
            {
                TokenKind = GeneratedValueTokenKind.OnlyIfTaken, SuffixStyle = GeneratedValueSuffixStyle.Number, SuffixStart = 1,
                AttemptLimit = 1000, NeverReuse = true, CollisionRemediation = true
            },
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = "Lower(mv[\"EmployeeId\"])" } }
        });
        await DbContext.SaveChangesAsync();
        return (ticketing, loginName);
    }

    private async Task<MetaverseAttribute> AddMetaverseAttributeAsync(MetaverseObjectType mvType, string name)
    {
        var attribute = new MetaverseAttribute
        {
            Name = name,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(attribute);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(attribute);
        return attribute;
    }

    private static void AddDirectFlow(SyncRule exportRule, ConnectedSystemObjectTypeAttribute target, MetaverseAttribute source) =>
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule, SyncRuleId = exportRule.Id,
            TargetConnectedSystemAttribute = target, TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
        });

    private async Task<ConnectedSystemObject> SeedHrPersonAsync(Context ctx, string first, string last, string employeeId)
    {
        var hrType = SyncRepo.ObjectTypes[ctx.HrCsoTypeId];
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ctx.Hr.Id,
            TypeId = hrType.Id,
            Type = hrType,
            ConnectedSystem = SyncRepo.ConnectedSystems[ctx.Hr.Id],
            Created = DateTime.UtcNow
        };
        foreach (var (name, value) in new[] { ("first", first), ("last", last), ("employeeId", employeeId) })
        {
            var attribute = hrType.Attributes.Single(a => a.Name == name);
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attribute.Id, Attribute = attribute, StringValue = value });
        }

        var externalId = hrType.Attributes.Single(a => a.IsExternalId);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalId.Id, Attribute = externalId, GuidValue = Guid.NewGuid() });
        SyncRepo.SeedConnectedSystemObject(cso);
        await Task.CompletedTask;
        return cso;
    }

    private void SeedContractorAccount(Context ctx, Guid metaverseObjectId, string uid)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ctx.Contractor.Id,
            ConnectedSystem = SyncRepo.ConnectedSystems[ctx.Contractor.Id],
            TypeId = ctx.ContractorCsoTypeId,
            MetaverseObjectId = metaverseObjectId,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = ctx.ContractorUid.Id, Attribute = ctx.ContractorUid, StringValue = uid });
        SyncRepo.SeedConnectedSystemObject(cso);
    }

    private SyncRuleMapping GeneratedMapping(Context ctx) =>
        SyncRepo.SyncRules[ctx.HrImportRuleId].AttributeFlowRules.Single(m => m.Generation != null);

    private ConnectedSystemObjectTypeAttribute DirectoryAttribute(Context ctx, string name) =>
        SyncRepo.ObjectTypes[ctx.SAMAccountName.ConnectedSystemObjectType.Id].Attributes.Single(a => a.Name == name);

    private MetaverseObject Mvo() => SyncRepo.MetaverseObjects.Values.Single();

    private string? AccountName(Context ctx) =>
        Mvo().AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.AccountName.Id)?.StringValue;

    private List<PendingExport> DirectoryExports(Context ctx) =>
        SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == ctx.Directory.Id).ToList();

    private PendingExport DirectoryExport(Context ctx) => DirectoryExports(ctx).Single();

    private List<ActivityRunProfileExecutionItem> ExecutionItems(Activity activity) =>
        SyncRepo.Rpeis.Values.Where(r => r.ActivityId == activity.Id)
            .Concat(activity.RunProfileExecutionItems)
            .DistinctBy(r => r.Id)
            .ToList();

    private static MockCallConnector RejectFirst(string? attributeName)
    {
        var rejected = false;
        return new MockCallConnector().WithConnectedSystemExportResultFactory(_ =>
        {
            if (rejected)
                return ConnectedSystemExportResult.Succeeded();
            rejected = true;
            return ConnectedSystemExportResult.ValueAlreadyInUse(AlreadyInUse, attributeName);
        });
    }

    private static MockCallConnector RejectAll(string? attributeName) =>
        new MockCallConnector().WithConnectedSystemExportResultFactory(_ => ConnectedSystemExportResult.ValueAlreadyInUse(AlreadyInUse, attributeName));

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    private async Task<Activity> RunDeltaSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();
        return activity;
    }

    private async Task<Activity> RunExportAsync(ConnectedSystem connectedSystem, MockCallConnector connector)
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, "Export", ConnectedSystemRunType.Export);
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.Export);
        var workerTask = new SynchronisationWorkerTask(reloaded.Id, profile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };
        await new SyncExportTaskProcessor(new SyncServer(Jim), SyncRepo, connector, reloaded, profile, workerTask, new CancellationTokenSource())
            .PerformExportAsync();
        return activity;
    }

    private async Task<(Activity Activity, List<LogEvent> Events)> RunExportCapturingLogsAsync(ConnectedSystem connectedSystem, MockCallConnector connector)
    {
        var sink = new CapturingSink();
        var original = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var activity = await RunExportAsync(connectedSystem, connector);
            return (activity, sink.Events);
        }
        finally
        {
            Log.Logger = original;
        }
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public List<LogEvent> Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
                _events.Add(logEvent);
        }
    }

    #endregion
}
