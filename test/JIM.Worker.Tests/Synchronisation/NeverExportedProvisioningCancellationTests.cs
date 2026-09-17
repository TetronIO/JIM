// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Workflows;
using Microsoft.EntityFrameworkCore;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// End-to-end regression tests, through the real synchronisation processor, for provisioning that is withdrawn
/// before it was ever exported. The engine provisions a Pending Provisioning CSO with a Create Pending Export;
/// if the Metaverse Object then leaves the export Synchronisation Rule's scope before any export runs, the
/// provisioning must be cancelled outright. It used to be replaced with a Delete Pending Export that carried no
/// identifier: the export failed ("Delete export has no External ID value"), and the CSO, holding no external
/// ID, was invisible to import deletion detection, so nothing could ever remove it.
/// </summary>
[TestFixture]
public class NeverExportedProvisioningCancellationTests : WorkflowTestBase
{
    [TestCase(OutboundDeprovisionAction.Delete)]
    [TestCase(OutboundDeprovisionAction.Disconnect)]
    public async Task FullSync_MvoLeavesScopeBeforeItsProvisioningWasExported_CancelsTheProvisioningAsync(OutboundDeprovisionAction deprovisionAction)
    {
        // Arrange: a source system feeding a Metaverse Object whose Status drives the scope of a provisioning
        // export Synchronisation Rule to a target system.
        var sourceSystem = await CreateConnectedSystemAsync("Cancel Source System");
        var targetSystem = await CreateConnectedSystemAsync("Cancel Target System");

        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "Status", Type = AttributeDataType.Text, Selected = true }
        });
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "User");

        var mvType = await CreateMvObjectTypeAsync("Person");
        var statusMvAttr = await AddMetaverseAttributeAsync(mvType, "Status");

        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "Cancel Source Import");
        var sourceStatusAttr = sourceType.Attributes.Single(a => a.Name == "Status");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = statusMvAttr,
            TargetMetaverseAttributeId = statusMvAttr.Id,
            Sources =
            {
                new SyncRuleMappingSource
                {
                    Order = 0,
                    ConnectedSystemAttribute = sourceStatusAttr,
                    ConnectedSystemAttributeId = sourceStatusAttr.Id
                }
            }
        });

        var exportRule = await CreateExportSyncRuleAsync(
            targetSystem.Id, targetType, mvType, "Cancel Target Export", enableProvisioning: true, deprovisionAction: deprovisionAction);

        // First, not Single: EF relationship fixup can add the attribute to the type's collection twice.
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var targetDisplayNameAttr = targetType.Attributes.Single(a => a.Name == "DisplayName");
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            Sources =
            {
                new SyncRuleMappingSource
                {
                    Order = 0,
                    MetaverseAttribute = mvDisplayNameAttr,
                    MetaverseAttributeId = mvDisplayNameAttr.Id
                }
            }
        });
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = statusMvAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "Active",
                    CaseSensitive = true
                }
            }
        });

        var sourceCso = await CreateCsoAsync(sourceSystem.Id, sourceType, "Noor Haddad", "EMP2001");
        var sourceStatusValue = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = sourceStatusAttr.Id,
            Attribute = sourceStatusAttr,
            StringValue = "Active"
        };
        sourceCso.AttributeValues.Add(sourceStatusValue);

        // In scope: the engine provisions a Pending Provisioning CSO with a Create that is never exported.
        await RunFullSyncAsync(sourceSystem, "Provisioning Full Sync");

        var provisionedCso = SyncRepo.ConnectedSystemObjects.Values.SingleOrDefault(cso => cso.ConnectedSystemId == targetSystem.Id);
        Assert.That(provisionedCso, Is.Not.Null, "Arrange: the in-scope Metaverse Object should have been provisioned to the target system.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(provisionedCso!.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning));
            Assert.That(PendingExportsFor(provisionedCso!.Id).Select(pe => pe.ChangeType), Is.EqualTo(new[] { PendingExportChangeType.Create }),
                "Arrange: the provisioning should be carried by a single, unsent Create Pending Export.");
        }

        // Act: leave scope before any export has run.
        sourceStatusValue.StringValue = "Inactive";
        await ModifyCsoAsync(sourceCso);
        var scopeOutActivity = await RunFullSyncAsync(sourceSystem, "Scope Out Full Sync");

        // Assert: nothing was ever in the target system, so nothing is left to export or to confirm away.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingExportsFor(provisionedCso!.Id), Is.Empty,
                "No Pending Export may remain: not the unsent Create, and above all not a Delete for an object that was never created.");
            Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(provisionedCso!.Id), Is.False,
                "The never-provisioned CSO must be removed; no import could ever confirm it away.");

            // The cancellation must still be visible on the Activity (Synchronisation Integrity: nothing
            // happens silently), as a root ProvisioningCancelled outcome naming the target Connected System.
            var provisioningCancelledOutcome = scopeOutActivity.RunProfileExecutionItems
                .SelectMany(rpei => rpei.SyncOutcomes)
                .SingleOrDefault(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled);
            Assert.That(provisioningCancelledOutcome, Is.Not.Null,
                "The scope-out cancellation must be reported on the Activity as a ProvisioningCancelled outcome");
            Assert.That(provisioningCancelledOutcome!.TargetEntityDescription, Is.EqualTo(targetSystem.Name),
                "The outcome must name the target Connected System the cancelled provisioning targeted");
        }
    }

    private List<PendingExport> PendingExportsFor(Guid connectedSystemObjectId) =>
        SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemObjectId == connectedSystemObjectId).ToList();

    /// <summary>
    /// Runs a Full Synchronisation on the given system with a fresh Run Profile and Activity.
    /// </summary>
    private async Task<Activity> RunFullSyncAsync(ConnectedSystem system, string runProfileName)
    {
        var runProfile = await CreateRunProfileAsync(system.Id, runProfileName, ConnectedSystemRunType.FullSynchronisation);
        system = await ReloadEntityAsync(system);
        var activity = await CreateActivityAsync(system.Id, runProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(
                new SyncEngine(), new SyncServer(Jim), SyncRepo, system, runProfile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    /// <summary>
    /// Adds a single-valued text Metaverse Attribute to the given Metaverse Object Type.
    /// </summary>
    private async Task<MetaverseAttribute> AddMetaverseAttributeAsync(MetaverseObjectType mvType, string name)
    {
        var attribute = new MetaverseAttribute
        {
            Name = name,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<PredefinedSearchAttribute>()
        };

        // Detach modified entities so EF only persists the new attribute (matching the base helpers).
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified).ToList())
            entry.State = EntityState.Detached;
        DbContext.MetaverseAttributes.Add(attribute);
        await DbContext.SaveChangesAsync();

        // EF relationship fixup may already have added the attribute via the MetaverseObjectTypes
        // navigation; only add it manually when it has not.
        if (!mvType.Attributes.Contains(attribute))
            mvType.Attributes.Add(attribute);

        return attribute;
    }
}
