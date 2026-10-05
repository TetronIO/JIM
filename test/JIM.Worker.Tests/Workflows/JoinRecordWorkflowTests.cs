// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// The durable join record (#348): a Connected System Object joined during inbound synchronisation records the
/// Synchronisation Rule responsible on itself, and loses the record when the join is broken. Export matching and
/// provisioning are covered alongside the rest of export evaluation, in <c>ExportEvaluationTests</c>.
/// </summary>
[TestFixture]
public class JoinRecordWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task FullSync_CsoProjected_RecordsProjectingRuleAsync()
    {
        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        var importRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        var cso = await CreateCsoAsync(hr.Id, hrType, "Jane Smith", "EMP001");

        await RunFullSyncAsync(hr);

        cso = await ReloadEntityAsync(cso);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Projected));
            Assert.That(cso.JoinSyncRuleId, Is.EqualTo(importRule.Id));
            Assert.That(cso.JoinSyncRuleName, Is.EqualTo("HR Import"));
        }
    }

    [Test]
    public async Task FullSync_CsoJoinedByImportRuleMatching_RecordsMatchingImportRuleAsync()
    {
        var (_, _, _, hrImportRule, hrCso) = await ArrangeDirectoryProjectsAndHrJoinsAsync(simpleMode: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hrCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined));
            Assert.That(hrCso.JoinSyncRuleId, Is.EqualTo(hrImportRule!.Id));
            Assert.That(hrCso.JoinSyncRuleName, Is.EqualTo("HR Import"));
        }
    }

    /// <summary>
    /// A Connected System with no import Synchronisation Rule for the object type joins on its own Object Matching
    /// Rules alone, so no Synchronisation Rule is responsible and none is recorded.
    /// </summary>
    [Test]
    public async Task FullSync_CsoJoinedByConnectedSystemMatchingWithNoImportRule_RecordsNoRuleAsync()
    {
        var (_, _, _, hrImportRule, hrCso) = await ArrangeDirectoryProjectsAndHrJoinsAsync(simpleMode: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hrImportRule, Is.Null);
            Assert.That(hrCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined));
            Assert.That(hrCso.JoinSyncRuleId, Is.Null);
            Assert.That(hrCso.JoinSyncRuleName, Is.Null);
        }
    }

    [Test]
    public async Task FullSync_CsoFallsOutOfScopeAndDisconnects_ClearsJoinRecordAsync()
    {
        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        var importRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        importRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria
                {
                    ConnectedSystemAttribute = hrType.Attributes.First(a => a.Name == "EmployeeId"),
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "EMP001",
                    CaseSensitive = true
                }
            ]
        });
        var cso = await CreateCsoAsync(hr.Id, hrType, "Jane Smith", "EMP001");
        await RunFullSyncAsync(hr);
        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.JoinSyncRuleId, Is.EqualTo(importRule.Id), "precondition: the projection is recorded");

        cso.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId").StringValue = "OUT_OF_SCOPE";
        cso.LastUpdated = DateTime.UtcNow;
        await RunFullSyncAsync(await ReloadEntityAsync(hr), "Full Sync 2");

        cso = await ReloadEntityAsync(cso);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.Null, "the object should have been disconnected");
            Assert.That(cso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.NotJoined));
            Assert.That(cso.JoinSyncRuleId, Is.Null);
            Assert.That(cso.JoinSyncRuleName, Is.Null);
        }
    }

    /// <summary>
    /// A Directory projects a person, then HR's object joins it on Employee ID: through an import Synchronisation
    /// Rule's own Object Matching Rule, or, in simple mode, through the HR object type's with no import rule at all.
    /// </summary>
    private async Task<(ConnectedSystem Hr, ConnectedSystemObjectType HrType, MetaverseObjectType MvType, SyncRule? HrImportRule, ConnectedSystemObject HrCso)>
        ArrangeDirectoryProjectsAndHrJoinsAsync(bool simpleMode)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "User");
        var directoryImportRule = await CreateImportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Import");
        var directoryEmployeeIdAttr = directoryType.Attributes.First(a => a.Name == "EmployeeId");
        directoryImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = directoryImportRule,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = directoryEmployeeIdAttr,
                ConnectedSystemAttributeId = directoryEmployeeIdAttr.Id
            }}
        });

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person");
        var hrEmployeeIdAttr = hrType.Attributes.First(a => a.Name == "EmployeeId");
        var matchingRule = new ObjectMatchingRule
        {
            Order = 0,
            CaseSensitive = true,
            MetaverseObjectType = mvType,
            MetaverseObjectTypeId = mvType.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = hrEmployeeIdAttr, ConnectedSystemAttributeId = hrEmployeeIdAttr.Id }]
        };

        SyncRule? hrImportRule = null;
        if (simpleMode)
        {
            hr.ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem;
            matchingRule.ConnectedSystemObjectType = hrType;
            matchingRule.ConnectedSystemObjectTypeId = hrType.Id;
            hrType.ObjectMatchingRules.Add(matchingRule);
        }
        else
        {
            hrImportRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import", enableProjection: false);
            matchingRule.SyncRule = hrImportRule;
            hrImportRule.ObjectMatchingRules.Add(matchingRule);
        }
        await DbContext.SaveChangesAsync();

        await CreateCsoAsync(directory.Id, directoryType, "Jane Smith", "EMP001");
        var hrCso = await CreateCsoAsync(hr.Id, hrType, "Jane Smith (HR)", "EMP001");

        await RunFullSyncAsync(directory);
        await RunFullSyncAsync(hr);

        hrCso = await ReloadEntityAsync(hrCso);
        Assert.That(hrCso.MetaverseObjectId, Is.Not.Null, "precondition: HR's object should have joined");
        return (hr, hrType, mvType, hrImportRule, hrCso);
    }

    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem, string profileName = "Full Sync")
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, profileName, ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(connectedSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, connectedSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }
}
