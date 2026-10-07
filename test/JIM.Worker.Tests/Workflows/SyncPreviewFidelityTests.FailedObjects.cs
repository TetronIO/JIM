// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Whole-system preview fidelity for an object the run fails outright (#1530). An inbound Expression that throws, or
/// one whose input is missing under a Missing Input Behaviour of Fail the object, errors the object and discards
/// everything its synchronisation would have done: nothing projects, flows or exports. The preview must propose the
/// error and nothing else, or it counts provisioning for objects the run will refuse.
/// </summary>
public partial class SyncPreviewFidelityTests
{
    [Test]
    public async Task PreviewFullSyncAsync_OfAnObjectTheRunFails_ProposesNothingElseForItAsync()
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

        var importRule = await CreateImportSyncRuleWithDisplayNameFlowAsync(source, sourceType, mvType);
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule, TargetMetaverseAttribute = mvEmployeeId, TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = "cs[\"EmployeeId\"]", MissingInputBehaviour = MissingInputBehaviour.FailObject } }
        });
        var exportRule = await CreateExportSyncRuleAsync(target.Id, targetType, mvType, "AD Export");
        FlowToAttribute(exportRule, targetType.Attributes.First(a => a.Name == "DisplayName"), mvDisplayName);
        await DbContext.SaveChangesAsync();
        await CreateCsoAsync(source.Id, sourceType, "John Smith", employeeId: null);

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(source.Id);
        await RunFullSyncAsync(source);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.MetaverseObjects, Is.Empty, "arrange: the run fails John Smith and projects nothing");
            Assert.That(CreatesStagedFor(target), Is.Zero, "arrange: so nothing is provisioned");
            Assert.That(preview.Counts.BlockedByErrors, Is.EqualTo(1), "the preview reports the failure");
            Assert.That(preview.Counts.ObjectsToCreate, Is.Zero, "and proposes no provisioning for an object the run refuses");
        }
    }
}
