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
/// The operators #2053 adds, driven through a real Full Synchronisation rather than the comparer alone: an import rule
/// scoped on "has no value" or "matches pattern" projects exactly the objects its criterion selects, and a pattern that
/// runs past its time limit on one object stops that object with an error while the rest of the run carries on.
/// </summary>
[TestFixture]
public class ScopingOperatorSynchronisationTests : WorkflowTestBase
{
    [Test]
    public async Task FullSync_ImportRuleScopedOnHasNoValue_ProjectsOnlyTheObjectsWithoutAValueAsync()
    {
        var ctx = await SetUpAsync(SearchComparisonType.HasNoValue, pattern: null,
            ("Contractor Without Type", "EMP001", null), ("Employee", "EMP002", "FTE"));

        var activity = await RunFullSyncAsync(ctx.System);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProjectedEmployeeIds(ctx), Is.EqualTo(new[] { "EMP001" }));
            Assert.That(Errors(activity), Is.Empty);
        }
    }

    [Test]
    public async Task FullSync_ImportRuleScopedOnMatchesPattern_ProjectsOnlyTheMatchingObjectsAsync()
    {
        // PRD Scenario 8's pattern, ignoring case: the upper-case address matches, the other domain does not.
        var ctx = await SetUpAsync(SearchComparisonType.MatchesPattern, @"^[a-z.]+@example\.org$",
            ("Alice", "EMP001", "alice@example.org"), ("Dave", "EMP002", "DAVE@Example.org"), ("Carol", "EMP003", "carol@other.com"));

        var activity = await RunFullSyncAsync(ctx.System);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProjectedEmployeeIds(ctx), Is.EqualTo(new[] { "EMP001", "EMP002" }));
            Assert.That(Errors(activity), Is.Empty);
        }
    }

    [Test]
    public async Task FullSync_PatternTimesOutOnOneObject_FailsThatObjectAndCompletesTheRestAsync()
    {
        // Catastrophic backtracking on the long value; the short one matches at once.
        var ctx = await SetUpAsync(SearchComparisonType.MatchesPattern, "^(a+)+$",
            ("Pathological", "EMP001", new string('a', 5000) + "!"), ("Ordinary", "EMP002", "aaaa"));

        var activity = await RunFullSyncAsync(ctx.System);

        var pathological = SyncRepo.ConnectedSystemObjects.Values.Single(c => EmployeeId(c) == "EMP001");
        var error = Errors(activity).SingleOrDefault(i => i.ConnectedSystemObjectId == pathological.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProjectedEmployeeIds(ctx), Is.EqualTo(new[] { "EMP002" }), "the rest of the run carries on");
            Assert.That(pathological.MetaverseObjectId, Is.Null, "an object whose scope could not be decided is left as it was");
            Assert.That(error?.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed));
            Assert.That(error?.ErrorMessage, Does.Contain("Mail").And.Contain("time limit"),
                "the error names the attribute and why, so the administrator can find the criterion");
        }
    }

    [Test]
    public async Task FullSync_PatternTimesOutOnAJoinedObject_LeavesItsJoinAsItWasAsync()
    {
        // The object projected under an earlier, fast pattern; the pattern then changes to one that times out on it.
        var ctx = await SetUpAsync(SearchComparisonType.MatchesPattern, "^a",
            ("Pathological", "EMP001", new string('a', 5000) + "!"));
        await RunFullSyncAsync(ctx.System);
        var cso = SyncRepo.ConnectedSystemObjects.Values.Single();
        var joinedTo = cso.MetaverseObjectId;
        Assert.That(joinedTo, Is.Not.Null, "precondition: the object projected under the fast pattern");

        ctx.Criterion.StringValue = "^(a+)+$";
        var activity = await RunFullSyncAsync(ctx.System);

        cso = SyncRepo.ConnectedSystemObjects.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.EqualTo(joinedTo), "a scope that cannot be decided must not disconnect the object");
            Assert.That(Errors(activity).Select(e => e.ErrorType), Is.EqualTo(new[] { ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed }));
        }
    }

    private async Task<Context> SetUpAsync(SearchComparisonType op, string? pattern, params (string Name, string EmployeeId, string? Value)[] objects)
    {
        var system = await CreateConnectedSystemAsync("HR");
        var csoType = await CreateCsoTypeAsync(system.Id, "Person");
        var valueAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "Mail", Type = AttributeDataType.Text, Selected = true, ConnectedSystemObjectType = csoType
        };
        DbContext.ConnectedSystemAttributes.Add(valueAttr);
        await DbContext.SaveChangesAsync();
        csoType.Attributes.Add(valueAttr);

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var csEmployeeIdAttr = csoType.Attributes.First(a => a.Name == "EmployeeId");

        var rule = await CreateImportSyncRuleAsync(system.Id, csoType, mvType, "HR Import");
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = csEmployeeIdAttr, ConnectedSystemAttributeId = csEmployeeIdAttr.Id } }
        });
        var criterion = new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = valueAttr,
            ComparisonType = op,
            StringValue = pattern,
            CaseSensitive = false
        };
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All, Criteria = [criterion] });
        await DbContext.SaveChangesAsync();

        foreach (var (name, employeeId, value) in objects)
        {
            var cso = await CreateCsoAsync(system.Id, csoType, name, employeeId);
            if (value != null)
            {
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    Id = Guid.NewGuid(), AttributeId = valueAttr.Id, Attribute = valueAttr, StringValue = value, ConnectedSystemObject = cso
                });
            }
        }

        return new Context(system, criterion, mvEmployeeIdAttr.Id);
    }

    private List<string> ProjectedEmployeeIds(Context ctx) => SyncRepo.MetaverseObjects.Values
        .Select(m => m.AttributeValues.Single(av => av.AttributeId == ctx.MvEmployeeIdAttributeId).StringValue!)
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToList();

    private static string? EmployeeId(ConnectedSystemObject cso) =>
        cso.AttributeValues.FirstOrDefault(av => av.Attribute?.Name == "EmployeeId")?.StringValue;

    private List<ActivityRunProfileExecutionItem> Errors(Activity activity) => SyncRepo.Rpeis.Values
        .Where(r => r.ActivityId == activity.Id)
        .Concat(activity.RunProfileExecutionItems)
        .DistinctBy(r => r.Id)
        .Where(r => r.ErrorType is not (null or ActivityRunProfileExecutionItemErrorType.NotSet))
        .ToList();

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, $"Full Sync {Guid.NewGuid():N}", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(connectedSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, connectedSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    private sealed record Context(ConnectedSystem System, SyncRuleScopingCriteria Criterion, int MvEmployeeIdAttributeId);
}
