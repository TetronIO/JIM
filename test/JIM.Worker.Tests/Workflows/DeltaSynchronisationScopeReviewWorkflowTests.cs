// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// The Temporal Scope Reconciler (#892) flags a Connected System Object whose data has not changed but whose scope the
/// clock has moved. Delta Synchronisation is the run a schedule makes most often, so it must act on the flag: an object
/// flagged as a leaver whose end date has passed is disconnected by the next Delta Synchronisation, not left until a
/// Full Synchronisation happens to run.
/// </summary>
[TestFixture]
public class DeltaSynchronisationScopeReviewWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task DeltaSync_ObjectFlaggedForScopeReviewAfterItsEndDatePassed_DisconnectsItAndClearsTheFlagAsync()
    {
        var source = await SetUpAsync(endDate: DateTime.UtcNow.AddSeconds(2));
        await RunAsync(source, ConnectedSystemRunType.FullSynchronisation);
        var cso = SyncRepo.ConnectedSystemObjects.Values.Single();
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "precondition: in scope and projected while the end date is ahead");

        await Task.Delay(TimeSpan.FromSeconds(3));
        var control = await RunAsync(source, ConnectedSystemRunType.DeltaSynchronisation);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null,
            "control: with nothing changed and nothing flagged, a Delta Synchronisation does not reach the object");
        Assert.That(control.ObjectsToProcess, Is.Zero);

        // The reconciler's sweep: the clock has carried the object out of scope.
        cso.ScopeReviewPending = true;

        var activity = await RunAsync(source, ConnectedSystemRunType.DeltaSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.Null, "the flagged object is re-evaluated and disconnected out of scope");
            Assert.That(ExecutionItems(activity).Any(r => r.ObjectChangeType == ObjectChangeType.DisconnectedOutOfScope), Is.True);
            Assert.That(cso.ScopeReviewPending, Is.False, "the flag is cleared once the object has been re-evaluated");
        }
    }

    /// <summary>
    /// One source system projecting a Person, with an import rule scoped to objects whose EndDate is still ahead.
    /// </summary>
    private async Task<ConnectedSystem> SetUpAsync(DateTime endDate)
    {
        var source = await CreateConnectedSystemAsync("HR");
        var externalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var employeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var endDateAttr = new ConnectedSystemObjectTypeAttribute { Name = "EndDate", Type = AttributeDataType.DateTime, Selected = true };
        var csoType = await CreateCsoTypeAsync(source.Id, "User",
            new List<ConnectedSystemObjectTypeAttribute> { externalIdAttr, displayNameAttr, employeeIdAttr, endDateAttr });

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");

        var rule = await CreateImportSyncRuleAsync(source.Id, csoType, mvType, "HR Import");
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = mvDisplayName,
            TargetMetaverseAttributeId = mvDisplayName.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = displayNameAttr, ConnectedSystemAttributeId = displayNameAttr.Id } }
        });
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria
                {
                    ConnectedSystemAttribute = endDateAttr,
                    ComparisonType = SearchComparisonType.GreaterThan,
                    ValueMode = DateCriteriaValueMode.Relative,
                    RelativeCount = 0,
                    RelativeUnit = RelativeDateUnit.Hours,
                    RelativeDirection = RelativeDateDirection.FromNow
                }
            ]
        });
        await DbContext.SaveChangesAsync();

        var cso = await CreateCsoAsync(source.Id, csoType, "John Smith", "EMP001", created: DateTime.UtcNow.AddMinutes(-10));
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = endDateAttr.Id, Attribute = endDateAttr, DateTimeValue = endDate, ConnectedSystemObject = cso
        });
        return source;
    }

    private List<ActivityRunProfileExecutionItem> ExecutionItems(Activity activity) => SyncRepo.Rpeis.Values
        .Where(r => r.ActivityId == activity.Id)
        .Concat(activity.RunProfileExecutionItems)
        .DistinctBy(r => r.Id)
        .ToList();

    private async Task<Activity> RunAsync(ConnectedSystem connectedSystem, ConnectedSystemRunType runType)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{runType} {Guid.NewGuid():N}", runType);
        var activity = await CreateActivityAsync(reloaded.Id, profile, runType);
        var cts = new CancellationTokenSource();
        if (runType == ConnectedSystemRunType.FullSynchronisation)
            await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, cts).PerformFullSyncAsync();
        else
            await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, cts).PerformDeltaSyncAsync();
        return activity;
    }
}
