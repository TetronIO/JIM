// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Workflow.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Workflow.Tests.Scenarios.Sync;

/// <summary>
/// An export rule scoped by a connection criterion (#2021, Phase 7a), through real synchronisation: the scope reads which
/// Object Types each Metaverse Object is joined to, in a batch per page, and moves when a join does. GAL sync's rules
/// read "has a user in PANOPLY AD"; here the same shape is "has a User in HR".
/// </summary>
[TestFixture]
public class ConnectionScopingWorkflowTests
{
    private WorkflowTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new WorkflowTestHarness();

    [TearDown]
    public void TearDown()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
            _harness.PrintSnapshotSummaries();
        _harness?.Dispose();
    }

    [Test]
    public async Task FullSync_ExportRuleScopedToPeopleWithAnHrAccount_ProvisionsEachOfThemAsync()
    {
        await SetUpAsync(ComparisonOf(has: true));
        _harness.GetConnector("HR").QueueImportObjects(Users("Ada", "Ben"));

        await _harness.ExecuteFullImportAsync("HR");
        await _harness.ExecuteFullSyncAsync("HR");
        var afterSync = await _harness.TakeSnapshotAsync("After Full Sync");

        Assert.That(afterSync.PendingExportCount, Is.EqualTo(2), "each person with an HR account is in scope, so each is provisioned");
    }

    [Test]
    public async Task FullSync_ExportRuleScopedToPeopleWithNoHrAccount_ProvisionsNobodyAsync()
    {
        await SetUpAsync(ComparisonOf(has: false));
        _harness.GetConnector("HR").QueueImportObjects(Users("Ada", "Ben"));

        await _harness.ExecuteFullImportAsync("HR");
        await _harness.ExecuteFullSyncAsync("HR");
        var afterSync = await _harness.TakeSnapshotAsync("After Full Sync");

        Assert.That(afterSync.PendingExportCount, Is.Zero, "everyone has an HR account, so nobody is in scope");
    }

    [TestCase(false, TestName = "FullSync_PersonLeavesHr_TheirAccountLeavesTheExportRulesScopeAsync")]
    [TestCase(true, TestName = "DeltaSync_PersonLeavesHr_TheirAccountLeavesTheExportRulesScopeAsync")]
    public async Task Sync_PersonLeavesHr_TheirAccountLeavesTheExportRulesScopeAsync(bool delta)
    {
        // The join to HR going is what takes Ben out of scope: no attribute of his changes, and his Metaverse Object stays.
        // Without re-evaluating scope on a join change, his AD account would stay provisioned for ever.
        await SetUpAsync(ComparisonOf(has: true));
        var hr = _harness.GetConnector("HR");
        var people = Users("Ada", "Ben");
        hr.QueueImportObjects(people);
        await _harness.ExecuteFullImportAsync("HR");
        await _harness.ExecuteFullSyncAsync("HR");
        await ProvisionAsync();

        hr.QueueImportObjects([people[0]]);
        await _harness.ExecuteFullImportAsync("HR");
        if (delta)
            await _harness.ExecuteDeltaSyncAsync("HR");
        else
            await _harness.ExecuteFullSyncAsync("HR");

        var after = await _harness.TakeSnapshotAsync("After Ben leaves HR");
        var accounts = after.GetCsos("AD").ToDictionary(cso => (string)cso.Attributes["cn"]!, cso => (Guid?)cso.Id);
        var deletes = after.PendingExports.Where(pe => pe.ChangeType == PendingExportChangeType.Delete).Select(pe => pe.ConnectedSystemObjectId).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.MvoCount, Is.EqualTo(2), "Ben's Metaverse Object stays: only his connection to HR went");
            Assert.That(deletes, Is.EquivalentTo(new[] { accounts["Ben"] }), "Ben left HR, so his AD account leaves scope and is deleted; Ada's stays");
        }
    }

    private static SearchComparisonType ComparisonOf(bool has) => has ? SearchComparisonType.HasValue : SearchComparisonType.HasNoValue;

    private async Task SetUpAsync(SearchComparisonType comparison)
    {
        await _harness.CreateConnectedSystemAsync("HR");
        await _harness.CreateObjectTypeAsync("HR", "User", t => t.WithGuidExternalId("employeeId").WithStringAttribute("displayName"));
        await _harness.CreateConnectedSystemAsync("AD");
        await _harness.CreateObjectTypeAsync("AD", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn"));
        var person = await _harness.CreateMetaverseObjectTypeAsync("Person", t => t.WithStringAttribute("displayName"));

        var hrUser = _harness.GetObjectType("HR", "User");
        var adUser = _harness.GetObjectType("AD", "User");
        var mvDisplayName = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "displayName");

        await _harness.CreateSyncRuleAsync("HR Import", "HR", "User", person, SyncRuleDirection.Import, r => r
            .WithProjection()
            .WithAttributeFlow(mvDisplayName, hrUser.Attributes.First(a => a.Name == "displayName")));

        var export = await _harness.CreateSyncRuleAsync("AD Export", "AD", "User", person, SyncRuleDirection.Export, r => r
            .WithProvisioning()
            .WithAttributeFlow(mvDisplayName, adUser.Attributes.First(a => a.Name == "cn"))
            .WithExpressionFlow("\"CN=\" + mv[\"displayName\"] + \",OU=Users,DC=test,DC=local\"",
                adUser.Attributes.First(a => a.Name == "distinguishedName")));
        export.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;
        export.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = [new SyncRuleScopingCriteria { ConnectedObjectType = hrUser, ConnectedObjectTypeId = hrUser.Id, ComparisonType = comparison }]
        });
        await _harness.DbContext.SaveChangesAsync();
    }

    private async Task ProvisionAsync()
    {
        await _harness.ExecuteExportAsync("AD");
        var ad = _harness.GetConnector("AD");
        ad.WithConfirmingImportFactory(pe => new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "User",
            Attributes =
            [
                new() { Name = "objectGUID", GuidValues = [Guid.NewGuid()] },
                new() { Name = "distinguishedName", StringValues = [pe.AttributeValueChanges.First(c => c.Attribute?.Name == "distinguishedName").StringValue!] },
                new() { Name = "cn", StringValues = [pe.AttributeValueChanges.First(c => c.Attribute?.Name == "cn").StringValue!] }
            ]
        });
        ad.QueueConfirmingImport();
        await _harness.ExecuteConfirmingImportAsync("AD");
    }

    private static List<ConnectedSystemImportObject> Users(params string[] names) => names.Select(name => new ConnectedSystemImportObject
    {
        ChangeType = ObjectChangeType.NotSet,
        ObjectType = "User",
        Attributes =
        [
            new() { Name = "employeeId", GuidValues = [Guid.NewGuid()] },
            new() { Name = "displayName", StringValues = [name] }
        ]
    }).ToList();
}
