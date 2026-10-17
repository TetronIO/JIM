// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// An Object Matching Rule that cannot be worked out stops the object (#2052). Before this, the matching engine logged
/// the failure and tried the next rule; with none left the object matched nothing and projected, so a broken rule
/// filled the Metaverse with duplicate identities and the only trace was a warning in a log.
/// </summary>
[TestFixture]
public class ObjectMatchingFailureWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task FullSync_MatchingRuleCannotBeEvaluated_RecordsTheErrorAndNeitherJoinsNorProjectsAsync()
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person");
        var importRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        importRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            Order = 0,
            SyncRule = importRule,
            MetaverseObjectType = mvType,
            MetaverseObjectTypeId = mvType.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            // Does not parse: the closing bracket is missing.
            Sources = [new ObjectMatchingRuleSource { Order = 0, Expression = "Trim(cs[\"EmployeeId\"]" }]
        });
        await DbContext.SaveChangesAsync();
        var cso = await CreateCsoAsync(hr.Id, hrType, "Jane Smith", "EMP001");

        var activity = await RunFullSyncAsync(hr);

        cso = await ReloadEntityAsync(cso);
        var items = SyncRepo.Rpeis.Values.Where(r => r.ActivityId == activity.Id)
            .Concat(activity.RunProfileExecutionItems)
            .DistinctBy(r => r.Id)
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.Null, "the object must not join on a rule that could not be evaluated");
            Assert.That(SyncRepo.MetaverseObjects, Is.Empty, "the object must not project a duplicate identity either");
            var item = items.SingleOrDefault(i => i.ConnectedSystemObjectId == cso.Id);
            Assert.That(item?.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ObjectMatchingRuleFailed));
            Assert.That(item?.ErrorMessage, Does.Contain("EmployeeId"));
        }
    }

    /// <summary>
    /// #2319: a schema refresh changed the source attribute's data type and no import has read it since, so the object
    /// holds its value only under the old type. A direct or an Expression rule over it cannot be worked out: the object
    /// is stopped with the error, never joined on a weaker rule and never projected as a duplicate of the identity it
    /// already has.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task FullSync_MatchingRuleSourceHeldOnlyUnderAnotherType_RecordsTheErrorAndNeitherJoinsNorProjectsAsync(bool expressionRule)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person");
        var importRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        var csEmployeeIdAttr = hrType.Attributes.First(a => a.Name == "EmployeeId");
        importRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            Order = 0,
            SyncRule = importRule,
            MetaverseObjectType = mvType,
            MetaverseObjectTypeId = mvType.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = expressionRule
                ? [new ObjectMatchingRuleSource { Order = 0, Expression = "cs[\"EmployeeId\"]" }]
                : [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = csEmployeeIdAttr, ConnectedSystemAttributeId = csEmployeeIdAttr.Id }]
        });
        await DbContext.SaveChangesAsync();
        var cso = await CreateCsoAsync(hr.Id, hrType, "Jane Smith", "1001");

        // The attribute is Text now, but the object still holds the number it was imported as.
        var heldValue = cso.AttributeValues.Single(av => av.AttributeId == csEmployeeIdAttr.Id);
        heldValue.StringValue = null;
        heldValue.IntValue = 1001;
        await DbContext.SaveChangesAsync();

        var activity = await RunFullSyncAsync(hr);

        cso = await ReloadEntityAsync(cso);
        var items = SyncRepo.Rpeis.Values.Where(r => r.ActivityId == activity.Id)
            .Concat(activity.RunProfileExecutionItems)
            .DistinctBy(r => r.Id)
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.Null, "the object must not join on a rule that could not be evaluated");
            Assert.That(SyncRepo.MetaverseObjects, Is.Empty, "the object must not project a duplicate identity either");
            var item = items.SingleOrDefault(i => i.ConnectedSystemObjectId == cso.Id);
            Assert.That(item?.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ObjectMatchingRuleFailed));
            Assert.That(item?.ErrorMessage, Does.Contain("EmployeeId").And.Contain("Number"));
        }
    }

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(connectedSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, connectedSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
