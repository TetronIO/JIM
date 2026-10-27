// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Whole-system preview fidelity where a target's values have drifted from what JIM holds (#1530, decision 4). A
/// Full Synchronisation corrects drift only in the system being synchronised, and only where its export rule enforces
/// state; a synchronisation of a source exports only what its own objects change. The preview of a Full
/// Synchronisation must propose what the run does, no more and no less, or an administrator reads a correction the
/// run will not make (or misses one it will).
/// </summary>
public partial class SyncPreviewFidelityTests
{
    [Test]
    public async Task PreviewFullSyncAsync_OfASourceWhoseTargetHasDrifted_ProposesNoUpdateTheRunDoesNotMakeAsync()
    {
        var ctx = await SetUpDriftAsync(enforceState: true);

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Source.Id);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Source));

        var realUpdates = UpdatesStagedFor(ctx.Target);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(realUpdates, Is.Zero, "arrange: a source's synchronisation does not correct a target's drift");
            Assert.That(preview.Counts.ObjectsToUpdate, Is.EqualTo(realUpdates),
                "the preview proposes the updates the run stages, and the run stages none");
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task PreviewFullSyncAsync_OfATargetThatHasDrifted_ProposesTheCorrectionsTheRunMakesAsync(bool enforceState)
    {
        var ctx = await SetUpDriftAsync(enforceState);

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Target.Id);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Target));

        var realUpdates = UpdatesStagedFor(ctx.Target);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(realUpdates, Is.EqualTo(enforceState ? 1 : 0),
                "arrange: the run corrects drift only where the export rule enforces state");
            Assert.That(preview.Counts.ObjectsToUpdate, Is.EqualTo(realUpdates),
                "the preview proposes the corrections the run stages");
        }
    }

    [Test]
    public async Task PreviewFullSyncAsync_OfADriftedTargetObjectUnchangedSinceTheLastSynchronisation_ProposesTheCorrectionTheRunMakesAsync()
    {
        var ctx = await SetUpDriftAsync(enforceState: true);

        // The drifted value was imported before the target's last synchronisation, and nothing has changed since. A Full
        // Synchronisation evaluates every object all the same (#891), so it corrects the drift, and the preview says so.
        var targetCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Target.Id);
        targetCso.Created = DateTime.UtcNow.AddHours(-2);
        targetCso.LastUpdated = DateTime.UtcNow.AddHours(-1);
        // ...and processed by that synchronisation, which cleared the mark its provisioning set (#2092).
        targetCso.SynchronisationPending = false;
        SyncRepo.ConnectedSystems[ctx.Target.Id].LastSyncCompletedAt = DateTime.UtcNow;

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Target.Id);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Target));

        var realUpdates = UpdatesStagedFor(ctx.Target);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(realUpdates, Is.EqualTo(1), "arrange: the run corrects the drift although the object is unchanged");
            Assert.That(preview.Counts.ObjectsToUpdate, Is.EqualTo(realUpdates), "the preview proposes the correction the run stages");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_OfADriftedTargetObject_ProposesTheCorrectionByAttributeAsync()
    {
        var ctx = await SetUpDriftAsync(enforceState: true);
        var targetCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Target.Id);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Target.Id, targetCso.Id);

        var correction = preview.Outbound.ProposedExports.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(correction.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
            Assert.That(correction.AttributeValueChanges, Is.Not.Empty);
            Assert.That(correction.AttributeValueChanges.Select(c => c.Attribute?.Name), Is.All.EqualTo("DisplayName"),
                "each change names the attribute it corrects, so the preview can show it");
            Assert.That(preview.OutcomeTree.Select(n => (n.OutcomeType, n.DetailCount)),
                Is.EqualTo(new[] { (ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection, (int?)1) }),
                "one drift correction root counting the drifted attribute, as the run records it");
        }
    }

    /// <summary>
    /// The run leaves an object holding a change until its Initial Password is delivered out of Drift Detection entirely
    /// (#2039), because a correction for any attribute would replace the Pending Export holding the change. The preview
    /// proposes no correction for it either, or an administrator reads a correction the run will not make.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_OfADriftedTargetObjectHoldingAChangeAwaitingInitialPassword_ProposesNoCorrectionTheRunDoesNotMakeAsync()
    {
        var ctx = await SetUpDriftAsync(enforceState: true);
        var targetCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Target.Id);
        var displayNameAttribute = targetCso.AttributeValues.Single(v => v.Attribute.Name == "DisplayName").Attribute;
        SyncRepo.SeedPendingExport(new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ctx.Target.Id,
            ConnectedSystemObjectId = targetCso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Exported,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    AttributeId = displayNameAttribute.Id,
                    Attribute = displayNameAttribute,
                    ChangeType = PendingExportAttributeChangeType.Update,
                    StringValue = "John Smith",
                    Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
                    ExportAttemptCount = 1,
                    AwaitsInitialPassword = true
                }
            ]
        });

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Target.Id, targetCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Target));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems.Concat(SyncRepo.Rpeis.Values)
                    .Any(i => i.ConnectedSystemObjectId == targetCso.Id && i.ObjectChangeType == ObjectChangeType.DriftCorrection),
                Is.False, "arrange: the run makes no drift correction for an object holding a change");
            Assert.That(preview.DriftCorrections, Is.Empty, "the preview proposes no correction the run does not make");
            Assert.That(preview.Outbound.ProposedExports, Is.Empty);
            Assert.That(preview.OutcomeTree.Select(n => n.OutcomeType),
                Has.No.Member(ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection));
        }
    }

    /// <summary>
    /// Where the enforced export expression fails, or Fail this mapping stops it, the run records the error against the
    /// object and stages no correction (#2180). The preview says the same, rather than failing itself or proposing a
    /// correction the run does not make.
    /// </summary>
    [TestCase("ToFileTime(42)", MissingInputBehaviour.EvaluateAnyway)]
    [TestCase("mv[\"Nickname\"]", MissingInputBehaviour.FailMapping)]
    [TestCase("mv[\"Nickname\"]", MissingInputBehaviour.FailObject)]
    public async Task PreviewSyncForCsoAsync_OfADriftedTargetObjectWhoseEnforcedExpressionCannotBeEvaluated_ReportsTheErrorAndProposesNoCorrectionAsync(
        string expression, MissingInputBehaviour missingInputBehaviour)
    {
        var ctx = await SetUpDriftAsync(enforceState: true);
        var source = ctx.ExportRule.AttributeFlowRules.Single().Sources.Single();
        source.MetaverseAttribute = null;
        source.MetaverseAttributeId = null;
        source.Expression = expression;
        source.MissingInputBehaviour = missingInputBehaviour;
        var targetCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Target.Id);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Target.Id, targetCso.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Errors.Any(e => e.Code == SyncPreviewMessageCode.ExpressionEvaluationError && e.AttributeName == "DisplayName"),
                Is.True, "the error the run records against the object is reported");
            Assert.That(preview.DriftCorrections, Is.Empty, "the run stages no correction");
            Assert.That(preview.Outbound.ProposedExports, Is.Empty);
        }
    }

    [Test]
    public async Task PreviewFullSyncAsync_OfATargetObjectThatWouldJoin_ProposesTheDriftCorrectionTheRunMakesAsync()
    {
        var source = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(source.Id, "User");
        var target = await CreateConnectedSystemAsync("AD Target");
        var targetType = await CreateCsoTypeAsync(target.Id, "user");
        var mvType = await CreateMvObjectTypeAsync("Person");
        mvType.Attributes.First(a => a.Name == "DisplayName").Name = Constants.BuiltInAttributes.DisplayName;
        await DbContext.SaveChangesAsync();
        var mvDisplayName = mvType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.DisplayName);
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var hrImport = await CreateImportSyncRuleWithDisplayNameFlowAsync(source, sourceType, mvType);
        var hrEmployeeId = sourceType.Attributes.First(a => a.Name == "EmployeeId");
        hrImport.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImport, TargetMetaverseAttribute = mvEmployeeId, TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrEmployeeId, ConnectedSystemAttributeId = hrEmployeeId.Id } }
        });

        // Active Directory joins on Employee ID and contributes nothing, so a display name edited there is drift.
        var adImport = await CreateImportSyncRuleAsync(target.Id, targetType, mvType, "AD Import", enableProjection: false);
        var adEmployeeId = targetType.Attributes.First(a => a.Name == "EmployeeId");
        adImport.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = adImport, SyncRuleId = adImport.Id, Order = 0, CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeId, TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = adEmployeeId, ConnectedSystemAttributeId = adEmployeeId.Id }]
        });
        var adExport = await CreateExportSyncRuleAsync(target.Id, targetType, mvType, "AD Export", enableProvisioning: false);
        adExport.EnforceState = true;
        FlowToAttribute(adExport, targetType.Attributes.First(a => a.Name == "DisplayName"), mvDisplayName);
        await DbContext.SaveChangesAsync();

        await CreateCsoAsync(source.Id, sourceType, "John Smith", "EMP001");
        var targetCso = await CreateCsoAsync(target.Id, targetType, "Edited in AD", "EMP001");
        await RunFullSyncAsync(source);
        SyncRepo.ClearAllPendingExports();
        Assert.That((await ReloadEntityAsync(targetCso)).MetaverseObjectId, Is.Null, "arrange: the target object is not joined yet");

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(target.Id);
        await RunFullSyncAsync(await ReloadEntityAsync(target));

        var realUpdates = UpdatesStagedFor(target);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(realUpdates, Is.EqualTo(1), "arrange: the run joins the object and corrects its drifted display name");
            Assert.That(preview.Counts.WouldJoin, Is.EqualTo(1));
            Assert.That(preview.Counts.ObjectsToUpdate, Is.EqualTo(realUpdates), "the preview proposes the correction the run stages");
        }
    }

    private sealed record DriftContext(ConnectedSystem Source, ConnectedSystem Target, SyncRule ExportRule);

    /// <summary>
    /// HR projects John Smith and flows his display name to a provisioned, confirmed Active Directory account, whose
    /// display name was then changed directly in Active Directory to "Edited in AD". Nothing has changed in HR.
    /// </summary>
    private async Task<DriftContext> SetUpDriftAsync(bool enforceState)
    {
        var source = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(source.Id, "User");
        var target = await CreateConnectedSystemAsync("AD Target");
        var targetType = await CreateCsoTypeAsync(target.Id, "user");
        var mvType = await CreateMvObjectTypeAsync("Person");
        mvType.Attributes.First(a => a.Name == "DisplayName").Name = Constants.BuiltInAttributes.DisplayName;
        await DbContext.SaveChangesAsync();
        var mvDisplayName = mvType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.DisplayName);

        await CreateImportSyncRuleWithDisplayNameFlowAsync(source, sourceType, mvType);
        var exportRule = await CreateExportSyncRuleAsync(target.Id, targetType, mvType, "AD Export");
        exportRule.EnforceState = enforceState;
        var targetDisplayName = targetType.Attributes.First(a => a.Name == "DisplayName");
        FlowToAttribute(exportRule, targetDisplayName, mvDisplayName);
        await DbContext.SaveChangesAsync();

        var sourceCso = await CreateCsoAsync(source.Id, sourceType, "John Smith", "EMP001");
        await RunFullSyncAsync(source);

        sourceCso = await ReloadEntityAsync(sourceCso);
        var mvoId = sourceCso.MetaverseObjectId ?? throw new InvalidOperationException("arrange: the first sync joins the source object");
        var targetCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.MetaverseObjectId == mvoId && c.Id != sourceCso.Id);
        targetCso.Status = ConnectedSystemObjectStatus.Normal;
        targetCso.AttributeValues.RemoveAll(av => av.AttributeId == targetDisplayName.Id);
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = targetCso, Attribute = targetDisplayName, AttributeId = targetDisplayName.Id,
            StringValue = "Edited in AD"
        });
        targetCso.LastUpdated = DateTime.UtcNow;
        SyncRepo.ClearAllPendingExports();

        return new DriftContext(source, target, exportRule);
    }

    private int UpdatesStagedFor(ConnectedSystem system) => SyncRepo.PendingExports.Values
        .Count(pe => pe.ConnectedSystemId == system.Id && pe.ChangeType == PendingExportChangeType.Update);
}
