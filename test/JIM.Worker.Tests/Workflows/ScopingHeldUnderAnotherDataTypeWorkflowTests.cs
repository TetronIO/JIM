// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// #2329: a schema refresh changed the scoped attribute's data type and no import has read it since, so the object holds
/// its value only under the old type. Its scope cannot be decided, so synchronisation stops that object with a
/// <see cref="ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed"/> error and acts on no guess: an object in
/// scope is not disconnected (nor its Metaverse Object deleted), and an object out of scope is not projected or joined.
/// </summary>
[TestFixture]
public class ScopingHeldUnderAnotherDataTypeWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task FullSync_JoinedObjectsScopedAttributeHeldUnderAnotherType_RecordsTheErrorAndKeepsTheJoinAsync()
    {
        var (hr, hrType, employeeId, importRule) = await SetUpAsync();
        Scope(importRule, employeeId, SearchComparisonType.Equals, "1001");
        var cso = await CreateCsoAsync(hr.Id, hrType, "Jane Smith", "1001");

        await RunFullSyncAsync(hr);
        cso = await ReloadEntityAsync(cso);
        var mvoId = cso.MetaverseObjectId;
        Assert.That(mvoId, Is.Not.Null, "precondition: the object is in scope and projected");

        HoldAsNumber(cso, employeeId, 1001);
        await ModifyCsoAsync(cso);
        var activity = await RunFullSyncAsync(hr);

        cso = await ReloadEntityAsync(cso);
        var item = ItemFor(activity, cso);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.EqualTo(mvoId), "an undecidable scope must not disconnect the object");
            Assert.That(SyncRepo.MetaverseObjects.ContainsKey(mvoId!.Value), Is.True, "nor delete its Metaverse Object");
            Assert.That(item?.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed));
            Assert.That(item?.ErrorMessage, Does.Contain("EmployeeId").And.Contain("Number").And.Contain("Full Import"));
        }
    }

    [Test]
    public async Task FullSync_UnjoinedObjectsScopedAttributeHeldUnderAnotherType_RecordsTheErrorAndNeitherProjectsNorJoinsAsync()
    {
        // "Does not equal" would bring an object with no value of the attribute into scope if a held value read as none.
        var (hr, hrType, employeeId, importRule) = await SetUpAsync();
        Scope(importRule, employeeId, SearchComparisonType.NotEquals, "9999");
        var cso = await CreateCsoAsync(hr.Id, hrType, "Jane Smith", "1001");
        HoldAsNumber(cso, employeeId, 1001);

        var activity = await RunFullSyncAsync(hr);

        cso = await ReloadEntityAsync(cso);
        var item = ItemFor(activity, cso);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.Null, "an undecidable scope must not join the object");
            Assert.That(SyncRepo.MetaverseObjects, Is.Empty, "nor project it");
            Assert.That(item?.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed));
            Assert.That(item?.ErrorMessage, Does.Contain("EmployeeId").And.Contain("Number"));
        }
    }

    [Test]
    public async Task FullSync_ScopedAttributeHoldsItsCurrentType_ProjectsAsBeforeAsync()
    {
        var (hr, hrType, employeeId, importRule) = await SetUpAsync();
        Scope(importRule, employeeId, SearchComparisonType.NotEquals, "9999");
        var cso = await CreateCsoAsync(hr.Id, hrType, "Jane Smith", "1001");

        var activity = await RunFullSyncAsync(hr);

        cso = await ReloadEntityAsync(cso);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.Not.Null);
            Assert.That(ItemFor(activity, cso)?.ErrorType ?? ActivityRunProfileExecutionItemErrorType.NotSet,
                Is.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet));
        }
    }

    private async Task<(ConnectedSystem Hr, ConnectedSystemObjectType HrType, ConnectedSystemObjectTypeAttribute EmployeeId, SyncRule ImportRule)> SetUpAsync()
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person");
        var importRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        return (hr, hrType, hrType.Attributes.First(a => a.Name == "EmployeeId"), importRule);
    }

    private static void Scope(SyncRule importRule, ConnectedSystemObjectTypeAttribute attribute, SearchComparisonType op, string value) =>
        importRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria
                {
                    ConnectedSystemAttribute = attribute, ConnectedSystemAttributeId = attribute.Id, ComparisonType = op, StringValue = value
                }
            ]
        });

    /// <summary>The attribute is Text now, but the object still holds the number it was imported as.</summary>
    private static void HoldAsNumber(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute, int value)
    {
        var held = cso.AttributeValues.Single(av => av.AttributeId == attribute.Id);
        held.StringValue = null;
        held.IntValue = value;
    }

    private ActivityRunProfileExecutionItem? ItemFor(Activity activity, ConnectedSystemObject cso) =>
        SyncRepo.Rpeis.Values.Where(r => r.ActivityId == activity.Id)
            .Concat(activity.RunProfileExecutionItems)
            .DistinctBy(r => r.Id)
            .SingleOrDefault(i => i.ConnectedSystemObjectId == cso.Id);

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(connectedSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, connectedSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
