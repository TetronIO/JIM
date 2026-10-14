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
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// An export rule's scoping criterion that cannot be evaluated for one Metaverse Object (a pattern running past its time
/// limit) stops that object with a <see cref="ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed"/> error:
/// nothing is provisioned, updated or deprovisioned for it, and every other object in the run carries on. Before this,
/// the exception escaped the page's export evaluation and failed the whole Activity.
/// </summary>
[TestFixture]
public class ScopingFailureExportTests : WorkflowTestBase
{
    private const string Pathological = "^(a+)+$";
    private static readonly string LongValue = new string('a', 5000) + "!";

    [Test]
    public async Task FullSync_ExportScopeTimesOutOnOneObject_ErrorsThatObjectAndExportsTheRestAsync()
    {
        var ctx = await SetUpAsync(Pathological, ("EMP001", LongValue), ("EMP002", "aaaa"));

        var activity = await RunFullSyncAsync(ctx.HrSystem);

        var pathologicalMvo = MvoFor(ctx, "EMP001");
        var error = Errors(activity).SingleOrDefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ExportedDisplayNames(ctx), Is.EqualTo(new[] { "aaaa" }), "the rest of the run exports");
            Assert.That(error?.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed));
            Assert.That(error?.ErrorMessage, Does.Contain("AD Export").And.Contain("DisplayName").And.Contain("time limit"),
                "the error names the rule and the attribute, so the administrator can find the criterion");
            Assert.That(error?.ErrorMessage, Does.Contain(pathologicalMvo.Id.ToString()), "the error names the object");
        }
    }

    [Test]
    public async Task FullSync_ExportScopeTimesOutOnAProvisionedObject_LeavesItProvisionedAsync()
    {
        // Provisioned under a fast pattern; the pattern then changes to one that times out on it. A scope that cannot be
        // decided must not deprovision the account.
        var ctx = await SetUpAsync("^a", ("EMP001", LongValue));
        await RunFullSyncAsync(ctx.HrSystem);
        Assert.That(ExportedDisplayNames(ctx), Is.EqualTo(new[] { LongValue }), "precondition: provisioned under the fast pattern");
        var provisionExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemId == ctx.TargetSystem.Id);
        var targetCsoId = provisionExport.ConnectedSystemObjectId;

        ctx.Criterion.StringValue = Pathological;
        ModifyDisplayName(ctx, "EMP001", LongValue + "?");
        var activity = await RunFullSyncAsync(ctx.HrSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity).Select(e => e.ErrorType), Is.EqualTo(new[] { ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed }));
            Assert.That(SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemObjectId == targetCsoId && pe.ChangeType == PendingExportChangeType.Delete),
                Is.Empty, "a scope that cannot be decided must not deprovision the account");
        }
    }

    private async Task<Context> SetUpAsync(string pattern, params (string EmployeeId, string DisplayName)[] people)
    {
        var hrSystem = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "Person");
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var importRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        importRule.AttributeFlowRules.Add(Flow(importRule, mvEmployeeId, hrType.Attributes.First(a => a.Name == "EmployeeId")));
        importRule.AttributeFlowRules.Add(Flow(importRule, mvDisplayName, hrType.Attributes.First(a => a.Name == "DisplayName")));

        var targetSystem = await CreateConnectedSystemAsync("AD");
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "user");
        var exportRule = await CreateExportSyncRuleAsync(targetSystem.Id, targetType, mvType, "AD Export",
            deprovisionAction: OutboundDeprovisionAction.Delete);
        var targetDisplayName = targetType.Attributes.First(a => a.Name == "DisplayName");
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayName,
            TargetConnectedSystemAttributeId = targetDisplayName.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvDisplayName, MetaverseAttributeId = mvDisplayName.Id } }
        });
        var criterion = new SyncRuleScopingCriteria
        {
            MetaverseAttribute = mvDisplayName,
            MetaverseAttributeId = mvDisplayName.Id,
            ComparisonType = SearchComparisonType.MatchesPattern,
            StringValue = pattern,
            CaseSensitive = true
        };
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All, Criteria = [criterion] });
        await DbContext.SaveChangesAsync();

        foreach (var (employeeId, displayName) in people)
            await CreateCsoAsync(hrSystem.Id, hrType, displayName, employeeId);

        return new Context(hrSystem, targetSystem, criterion, mvEmployeeId.Id, mvDisplayName.Id, targetDisplayName.Id);
    }

    private static SyncRuleMapping Flow(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source) => new()
    {
        SyncRule = rule,
        TargetMetaverseAttribute = target,
        TargetMetaverseAttributeId = target.Id,
        Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
    };

    private MetaverseObject MvoFor(Context ctx, string employeeId) => SyncRepo.MetaverseObjects.Values
        .Single(m => m.AttributeValues.Any(av => av.AttributeId == ctx.MvEmployeeIdAttributeId && av.StringValue == employeeId));

    private void ModifyDisplayName(Context ctx, string employeeId, string displayName)
    {
        var cso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.HrSystem.Id &&
            c.AttributeValues.Any(av => av.Attribute?.Name == "EmployeeId" && av.StringValue == employeeId));
        cso.AttributeValues.Single(av => av.Attribute?.Name == "DisplayName").StringValue = displayName;
    }

    private List<string> ExportedDisplayNames(Context ctx) => SyncRepo.PendingExports.Values
        .Where(pe => pe.ConnectedSystemId == ctx.TargetSystem.Id && pe.ChangeType == PendingExportChangeType.Create)
        .SelectMany(pe => pe.AttributeValueChanges)
        .Where(change => change.AttributeId == ctx.TargetDisplayNameAttributeId)
        .Select(change => change.StringValue!)
        .Order(StringComparer.Ordinal)
        .ToList();

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

    private sealed record Context(ConnectedSystem HrSystem, ConnectedSystem TargetSystem, SyncRuleScopingCriteria Criterion,
        int MvEmployeeIdAttributeId, int MvDisplayNameAttributeId, int TargetDisplayNameAttributeId);
}
