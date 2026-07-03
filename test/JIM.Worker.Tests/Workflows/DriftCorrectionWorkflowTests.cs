// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Enums;
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
        // --- Source system: user type with DisplayName (imported) and Description (enforced by export policy) ---
        var system = await CreateConnectedSystemAsync("Drift Source");
        var externalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var descriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "Description", Type = AttributeDataType.Text, Selected = true };
        var csoType = await CreateCsoTypeAsync(system.Id, "User",
            new List<ConnectedSystemObjectTypeAttribute> { externalIdAttr, displayNameAttr, descriptionAttr });

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");

        // --- Import rule: projects to the Metaverse, flows DisplayName ---
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

        // --- Export rule with EnforceState: Description must equal a constant the system does not contribute ---
        // A constant expression source keeps the system a non-contributor for the enforced attribute, so drift
        // detection fires on the very first sync, on the same page that projects the Metaverse Object.
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
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = "\"" + EnforcedValue + "\"" } }
        });
        DbContext.SyncRules.Add(exportRule);
        await DbContext.SaveChangesAsync();
        SyncRepo.SeedSyncRule(exportRule);

        // --- CSO whose Description has drifted from the enforced value ---
        var cso = await CreateCsoAsync(system.Id, csoType, "Drift User");
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = descriptionAttr.Id,
            Attribute = descriptionAttr,
            StringValue = DriftedValue,
            ConnectedSystemObject = cso
        });

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
        Assert.That(correctivePe.SourceMetaverseObjectId, Is.EqualTo(mvo.Id),
            "the corrective Pending Export must reference the projected Metaverse Object");
    }

    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }
}
