// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Workflow tests for drift correction interacting with projection. Corrective Pending Exports are staged
/// during Connected System Object processing, but a Metaverse Object projected on the same page has no
/// database identity until the page flush persists it. If drift correction captures the Metaverse Object's
/// id by value before persistence, the corrective Pending Export carries Guid.Empty in
/// SourceMetaverseObjectId and the batch insert violates the foreign key to MetaverseObjects (23503).
/// </summary>
[TestFixture]
public class DriftCorrectionWorkflowTests : WorkflowTestBase
{
    private const string EnforcedValue = "ENFORCED";
    private const string DriftedValue = "locally-changed-value";

    [Test]
    public async Task FullSync_DriftOnJustProjectedMvo_CorrectivePendingExportCarriesPersistedMvoIdAsync()
    {
        var (system, cso, _) = await ArrangeDriftedObjectAsync();

        // --- Act: one full sync projects the Metaverse Object AND detects the drift on the same page ---
        await RunFullSyncAsync(system);

        // --- Assert: the projected MVO exists and every staged Pending Export references its persisted id ---
        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        Assert.That(mvo.Id, Is.Not.EqualTo(Guid.Empty), "the projected Metaverse Object must have a persisted id");

        var pendingExports = await SyncRepo.GetPendingExportsAsync(system.Id);
        var correctivePe = pendingExports.SingleOrDefault(pe =>
            pe.ConnectedSystemObjectId == cso.Id && pe.ChangeType == PendingExportChangeType.Update);
        Assert.That(correctivePe, Is.Not.Null, "drift detection must stage a corrective Pending Export for the drifted CSO");

        Assert.That(correctivePe!.SourceMetaverseObjectId, Is.Not.EqualTo(Guid.Empty),
            "the corrective Pending Export must not capture the Metaverse Object id before it is persisted; " +
            "a Guid.Empty source violates FK_PendingExports_MetaverseObjects_SourceMetaverseObjectId at flush");
        Assert.That(correctivePe!.SourceMetaverseObjectId, Is.EqualTo(mvo.Id),
            "the corrective Pending Export must reference the projected Metaverse Object");
    }

    /// <summary>
    /// Drift on an object holding a change until its Initial Password is delivered (#2039) is corrected at once, not a
    /// synchronisation later (#2501). The correction replaces the object's Pending Export at the page flush, so it
    /// carries the held change over exactly as it was: still held, still awaiting confirmation, its attempt count kept.
    /// Dropping it would have the next evaluation re-send the enabled value Active Directory refuses without a password.
    /// </summary>
    [Test]
    public async Task FullSync_DriftOnAnObjectWithAChangeAwaitingInitialPassword_CorrectsTheDriftAndKeepsTheHeldChangeAsync()
    {
        var (system, cso, displayNameAttr) = await ArrangeDriftedObjectAsync();
        SeedHeldChange(system, cso, displayNameAttr, "Drift User");

        var activity = await RunFullSyncAsync(system);

        var changesForObject = await PendingChangesForAsync(system, cso);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changesForObject.Count(c => c.AttributeId == displayNameAttr.Id), Is.EqualTo(1),
                "the held change is carried over once, not duplicated");
            Assert.That(changesForObject.Any(c => c.AttributeId == displayNameAttr.Id && c.AwaitsInitialPassword &&
                    c.Status == PendingExportAttributeChangeStatus.ExportedPendingConfirmation && c.ExportAttemptCount == 1 &&
                    c.StringValue == "Drift User"),
                Is.True, "the held change must survive the page flush exactly as it was");
            Assert.That(changesForObject.Any(c => c.StringValue == EnforcedValue && !c.AwaitsInitialPassword &&
                    c.Status == PendingExportAttributeChangeStatus.Pending),
                Is.True, "the drifted attribute is corrected now, not a synchronisation later");
            Assert.That(activity.RunProfileExecutionItems.Concat(SyncRepo.Rpeis.Values)
                    .Any(i => i.ConnectedSystemObjectId == cso.Id && i.ObjectChangeType == ObjectChangeType.DriftCorrection),
                Is.True, "the correction is reported as a Drift Correction");
        }
    }

    /// <summary>
    /// Drift on the held attribute itself is no correction at all where the held change already asserts what the
    /// Metaverse Object wants (#2039, #2501): the target holds its interim value until the Initial Password is delivered.
    /// Staged, the correction would supersede the held change and re-send the value Active Directory refuses
    /// (<c>0000052D</c>), so nothing is staged and nothing is reported.
    /// </summary>
    [Test]
    public async Task FullSync_DriftOnlyOnTheHeldAttribute_RestatingTheHeldChange_StagesNothingAndKeepsTheHeldChangeAsync()
    {
        var (system, cso, _) = await ArrangeDriftedObjectAsync();
        var descriptionAttr = cso.AttributeValues.Single(v => v.StringValue == DriftedValue).Attribute;
        SeedHeldChange(system, cso, descriptionAttr, EnforcedValue);

        var activity = await RunFullSyncAsync(system);

        var changesForObject = await PendingChangesForAsync(system, cso);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changesForObject, Has.Count.EqualTo(1), "only the held change remains");
            Assert.That(changesForObject.Single().AwaitsInitialPassword, Is.True);
            Assert.That(changesForObject.Single().Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(changesForObject.Single().ExportAttemptCount, Is.EqualTo(1));
            Assert.That(activity.RunProfileExecutionItems.Concat(SyncRepo.Rpeis.Values)
                    .Any(i => i.ConnectedSystemObjectId == cso.Id && i.ObjectChangeType == ObjectChangeType.DriftCorrection),
                Is.False, "restating the held change corrects nothing, so no Drift Correction is reported");
        }
    }

    private void SeedHeldChange(ConnectedSystem system, ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute, string value) =>
        SyncRepo.SeedPendingExport(new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectId = cso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Exported,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    AttributeId = attribute.Id,
                    Attribute = attribute,
                    ChangeType = PendingExportAttributeChangeType.Update,
                    StringValue = value,
                    Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
                    ExportAttemptCount = 1,
                    AwaitsInitialPassword = true
                }
            ]
        });

    private async Task<List<PendingExportAttributeValueChange>> PendingChangesForAsync(ConnectedSystem system, ConnectedSystemObject cso) =>
        (await SyncRepo.GetPendingExportsAsync(system.Id))
            .Where(pe => pe.ConnectedSystemObjectId == cso.Id)
            .SelectMany(pe => pe.AttributeValueChanges)
            .ToList();

    /// <summary>
    /// Control for the test above: the same drifted object with nothing held is corrected as usual, so the skip is
    /// what the held change causes and nothing else.
    /// </summary>
    [Test]
    public async Task FullSync_DriftOnAnObjectWithNothingHeld_StagesTheCorrectionAsync()
    {
        var (system, cso, _) = await ArrangeDriftedObjectAsync();

        var activity = await RunFullSyncAsync(system);

        Assert.That(activity.RunProfileExecutionItems.Concat(SyncRepo.Rpeis.Values)
                .Any(i => i.ConnectedSystemObjectId == cso.Id && i.ObjectChangeType == ObjectChangeType.DriftCorrection),
            Is.True);
    }

    /// <summary>
    /// An enforced export expression that fails is reported against the Connected System Object (#2180), never only
    /// logged, and the target keeps its value: no correction is staged, and the run carries on.
    /// </summary>
    [Test]
    public async Task FullSync_EnforcedExpressionFails_RecordsAnExpressionErrorForTheObjectAndStagesNoCorrectionAsync()
    {
        var (system, cso, _) = await ArrangeDriftedObjectAsync(descriptionExpression: "ToFileTime(42)");

        var activity = await RunFullSyncAsync(system);

        await AssertDriftErrorRecordedAndNothingCorrectedAsync(activity, system, cso, ActivityRunProfileExecutionItemErrorType.ExpressionEvaluationError);
    }

    /// <summary>
    /// Missing Input Behaviour of Fail the object applies to Drift Detection as it does to export staging (#2180).
    /// </summary>
    [Test]
    public async Task FullSync_EnforcedExpressionMissingAnInputWithFailObject_RecordsTheMissingInputForTheObjectAndStagesNoCorrectionAsync()
    {
        var (system, cso, _) = await ArrangeDriftedObjectAsync(
            descriptionExpression: "mv[\"DisplayName\"] + \" \" + mv[\"Nickname\"]", missingInputBehaviour: MissingInputBehaviour.FailObject);

        var activity = await RunFullSyncAsync(system);

        await AssertDriftErrorRecordedAndNothingCorrectedAsync(activity, system, cso, ActivityRunProfileExecutionItemErrorType.ExpressionMissingInput);
    }

    /// <summary>
    /// A mapping-level error Drift Detection finds (here Missing Input Behaviour of Fail this mapping) is recorded on a
    /// Run Profile Execution Item for the object, exactly as export staging records it (#2180).
    /// </summary>
    [Test]
    public async Task FullSync_EnforcedExpressionMissingAnInputWithFailMapping_RecordsTheMappingErrorForTheObjectAndStagesNoCorrectionAsync()
    {
        var (system, cso, _) = await ArrangeDriftedObjectAsync(
            descriptionExpression: "mv[\"DisplayName\"] + \" \" + mv[\"Nickname\"]", missingInputBehaviour: MissingInputBehaviour.FailMapping);

        var activity = await RunFullSyncAsync(system);

        await AssertDriftErrorRecordedAndNothingCorrectedAsync(activity, system, cso, ActivityRunProfileExecutionItemErrorType.ExpressionMissingInput);
    }

    private async Task AssertDriftErrorRecordedAndNothingCorrectedAsync(JIM.Models.Activities.Activity activity, ConnectedSystem system,
        ConnectedSystemObject cso, ActivityRunProfileExecutionItemErrorType expectedErrorType)
    {
        var items = activity.RunProfileExecutionItems.Concat(SyncRepo.Rpeis.Values).ToList();
        var correctionsForObject = (await SyncRepo.GetPendingExportsAsync(system.Id))
            .Where(pe => pe.ConnectedSystemObjectId == cso.Id)
            .SelectMany(pe => pe.AttributeValueChanges)
            .ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(items.Any(i => i.ConnectedSystemObjectId == cso.Id && i.ErrorType == expectedErrorType), Is.True,
                "the failure must be recorded against the Connected System Object, not only logged");
            Assert.That(items.Any(i => i.ConnectedSystemObjectId == cso.Id && i.ObjectChangeType == ObjectChangeType.DriftCorrection), Is.False,
                "nothing was corrected, so no Drift Correction is reported");
            Assert.That(correctionsForObject, Is.Empty, "the target keeps its value: nothing is staged to clear or overwrite it");
            Assert.That(items.Any(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError), Is.False,
                "an expression fault is the object's error, never an unhandled one");
        }
    }

    /// <summary>
    /// The arrangement of the first test in this fixture: a projecting import rule, an enforcing export rule whose
    /// constant Description the object has drifted from, and the drifted object. The Description expression and its
    /// Missing Input Behaviour can be replaced, to drive the export expression's failure modes.
    /// </summary>
    private async Task<(ConnectedSystem System, ConnectedSystemObject Cso, ConnectedSystemObjectTypeAttribute DisplayNameAttr)> ArrangeDriftedObjectAsync(
        string? descriptionExpression = null,
        MissingInputBehaviour missingInputBehaviour = MissingInputBehaviour.EvaluateAnyway)
    {
        var system = await CreateConnectedSystemAsync("Drift Source");
        var externalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var descriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "Description", Type = AttributeDataType.Text, Selected = true };
        var csoType = await CreateCsoTypeAsync(system.Id, "User",
            new List<ConnectedSystemObjectTypeAttribute> { externalIdAttr, displayNameAttr, descriptionAttr });

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");

        var importRule = await CreateImportSyncRuleAsync(system.Id, csoType, mvType, "Drift Import");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = displayNameAttr, ConnectedSystemAttributeId = displayNameAttr.Id } }
        });
        await DbContext.SaveChangesAsync();

        // Export rule with EnforceState: Description must equal a constant the system does not contribute. A constant
        // expression source keeps the system a non-contributor for the enforced attribute, so drift detection fires on
        // the very first sync, on the same page that projects the Metaverse Object.
        var exportRule = new SyncRule
        {
            ConnectedSystemId = system.Id,
            Name = "Drift Export Policy",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            EnforceState = true,
            ConnectedSystemObjectTypeId = csoType.Id,
            ConnectedSystemObjectType = csoType,
            MetaverseObjectTypeId = mvType.Id,
            MetaverseObjectType = mvType
        };
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = descriptionAttr,
            TargetConnectedSystemAttributeId = descriptionAttr.Id,
            Sources =
            {
                new SyncRuleMappingSource
                {
                    Order = 0,
                    Expression = descriptionExpression ?? "\"" + EnforcedValue + "\"",
                    MissingInputBehaviour = missingInputBehaviour
                }
            }
        });
        DbContext.SyncRules.Add(exportRule);
        await DbContext.SaveChangesAsync();
        SyncRepo.SeedSyncRule(exportRule);

        var cso = await CreateCsoAsync(system.Id, csoType, "Drift User");
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = descriptionAttr.Id,
            Attribute = descriptionAttr,
            StringValue = DriftedValue,
            ConnectedSystemObject = cso
        });

        return (system, cso, displayNameAttr);
    }

    private async Task<JIM.Models.Activities.Activity> RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
