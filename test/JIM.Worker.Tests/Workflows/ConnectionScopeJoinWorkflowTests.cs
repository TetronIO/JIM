// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A join that flows no attribute can still move an export rule's scope, when the rule has a connection criterion (#2021).
/// Export evaluation used to be queued only for objects whose attributes changed, so a person whose HR record joined them
/// without changing a value would never have been provisioned by a rule scoped to "has a Person in HR".
/// </summary>
[TestFixture]
public class ConnectionScopeJoinWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task FullSync_JoinFlowsNoAttribute_ProvisionsThePersonNowInScopeAsync()
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");

        // The Directory projects Jane and flows her EmployeeId.
        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "User");
        var directoryImport = await CreateImportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Import");
        directoryImport.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = directoryImport,
            TargetMetaverseAttribute = mvEmployeeId,
            TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = directoryType.Attributes.First(a => a.Name == "EmployeeId"),
                ConnectedSystemAttributeId = directoryType.Attributes.First(a => a.Name == "EmployeeId").Id } }
        });

        // HR joins on EmployeeId through its own Object Matching Rule, with no import rule: the join flows nothing.
        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person");
        hr.ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem;
        hrType.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            Order = 0,
            CaseSensitive = true,
            MetaverseObjectType = mvType,
            MetaverseObjectTypeId = mvType.Id,
            TargetMetaverseAttribute = mvEmployeeId,
            TargetMetaverseAttributeId = mvEmployeeId.Id,
            ConnectedSystemObjectType = hrType,
            ConnectedSystemObjectTypeId = hrType.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = hrType.Attributes.First(a => a.Name == "EmployeeId"),
                ConnectedSystemAttributeId = hrType.Attributes.First(a => a.Name == "EmployeeId").Id }]
        });

        // The Badge system takes everyone with an HR record.
        var badge = await CreateConnectedSystemAsync("Badge");
        var badgeType = await CreateCsoTypeAsync(badge.Id, "Holder");
        var badgeExport = await CreateExportSyncRuleAsync(badge.Id, badgeType, mvType, "Badge Export");
        badgeExport.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = badgeExport,
            TargetConnectedSystemAttribute = badgeType.Attributes.First(a => a.Name == "EmployeeId"),
            TargetConnectedSystemAttributeId = badgeType.Attributes.First(a => a.Name == "EmployeeId").Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvEmployeeId, MetaverseAttributeId = mvEmployeeId.Id } }
        });
        badgeExport.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = [new SyncRuleScopingCriteria { ConnectedObjectType = hrType, ConnectedObjectTypeId = hrType.Id, ComparisonType = SearchComparisonType.HasValue }]
        });
        await DbContext.SaveChangesAsync();

        await CreateCsoAsync(directory.Id, directoryType, "Jane Smith", "EMP001");
        var hrCso = await CreateCsoAsync(hr.Id, hrType, "Jane Smith (HR)", "EMP001");

        await RunFullSyncAsync(directory);
        Assert.That(PendingExportsFor(badge), Is.Empty, "arrange: Jane has no HR record yet, so she is out of scope");

        await RunFullSyncAsync(hr);

        hrCso = await ReloadEntityAsync(hrCso);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hrCso.MetaverseObjectId, Is.Not.Null, "precondition: HR's record joined Jane");
            Assert.That(PendingExportsFor(badge).Select(pe => pe.ChangeType), Is.EqualTo(new[] { PendingExportChangeType.Create }),
                "the join brought Jane into scope, so her badge is provisioned");
        }
    }

    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(connectedSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, connectedSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }

    private List<PendingExport> PendingExportsFor(ConnectedSystem system) =>
        SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == system.Id).ToList();
}
