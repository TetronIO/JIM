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
/// A Full Synchronisation is the administrator's remediation tool: it must re-evaluate every Connected System Object
/// against the Synchronisation Rules, independent of the timestamps and marks Delta Synchronisation relies on to pick
/// its objects (#891). Each test here leaves an object whose own data and timestamps say "unchanged since the last
/// synchronisation" while the Metaverse or the clock says otherwise, and expects a Full Synchronisation to put it right.
/// </summary>
[TestFixture]
public class FullSynchronisationEvaluatesEveryObjectWorkflowTests : WorkflowTestBase
{
    private const string JohnSmith = "John Smith";

    [Test]
    public async Task FullSync_MetaverseValueAlteredOutsideSynchronisation_RestoresItAsync()
    {
        var ctx = await SetUpSourceAsync();
        await RunFullSyncAsync(ctx.Source);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var displayName = mvo.AttributeValues.Single(av => av.AttributeId == ctx.MvDisplayNameAttributeId);
        Assert.That(displayName.StringValue, Is.EqualTo(JohnSmith), "precondition: the value flowed in");

        // Damage the Metaverse without touching the Connected System Object, as a defect, a restore or a manual
        // database repair would. Nothing about the object's own data or timestamps changes.
        displayName.StringValue = "Corrupted";

        await RunFullSyncAsync(ctx.Source);

        Assert.That(SyncRepo.MetaverseObjects.Values.Single().AttributeValues
                .Single(av => av.AttributeId == ctx.MvDisplayNameAttributeId).StringValue,
            Is.EqualTo(JohnSmith), "a Full Synchronisation must re-flow the contributing object's value");
    }

    [Test]
    public async Task FullSync_ChangeStampedBelowWatermarkWithoutMark_IsProcessedAsync()
    {
        var ctx = await SetUpSourceAsync();
        await RunFullSyncAsync(ctx.Source);
        var watermark = (await ReloadEntityAsync(ctx.Source)).LastSyncCompletedAt;
        Assert.That(watermark, Is.Not.Null, "precondition: the first run set the watermark");

        // The #2092 shape with its safety net missing: a change whose timestamp sits below the watermark the last run
        // wrote, and no synchronisation-pending mark. Delta Synchronisation cannot see it; a Full Synchronisation must.
        var cso = SyncRepo.ConnectedSystemObjects.Values.Single();
        cso.AttributeValues.Single(av => av.AttributeId == ctx.CsDisplayNameAttributeId).StringValue = "Jane Smith";
        cso.LastUpdated = watermark!.Value.AddSeconds(-1);
        cso.SynchronisationPending = false;

        await RunFullSyncAsync(ctx.Source);

        Assert.That(SyncRepo.MetaverseObjects.Values.Single().AttributeValues
                .Single(av => av.AttributeId == ctx.MvDisplayNameAttributeId).StringValue,
            Is.EqualTo("Jane Smith"), "a Full Synchronisation must not depend on the change having been marked");
    }

    [Test]
    public async Task FullSync_RelativeDateScopeBoundaryPassedWithStaticData_DisconnectsWithoutReconcilerAsync()
    {
        // In scope while its end date is in the future; the clock, not the data, moves it out.
        var ctx = await SetUpSourceAsync(endDate: DateTime.UtcNow.AddSeconds(2), scopeToFutureEndDate: true);
        await RunFullSyncAsync(ctx.Source);
        var cso = SyncRepo.ConnectedSystemObjects.Values.Single();
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "precondition: in scope and projected while the end date is ahead");

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.That(cso.ScopeReviewPending, Is.False, "precondition: the Temporal Scope Reconciler has not flagged it");

        var activity = await RunFullSyncAsync(ctx.Source);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.Null,
                "a Full Synchronisation evaluates scope for every object, so the clock-driven exit is applied");
            Assert.That(ExecutionItems(activity).Any(r => r.ObjectChangeType == ObjectChangeType.DisconnectedOutOfScope), Is.True,
                "the disconnection is recorded on an execution item");
        }
    }

    [Test]
    public async Task FullSync_RepeatedWithNothingChanged_ChangesNothingAsync()
    {
        // Guard against evaluation that is not idempotent: once every object is evaluated on every run, a mapping
        // that re-stages an identical value would surface as Metaverse churn or Pending Exports on every run.
        var ctx = await SetUpSourceAsync(withTarget: true);
        await RunFullSyncAsync(ctx.Source);

        var pendingExportsBefore = SyncRepo.PendingExports.Values
            .Select(pe => (pe.Id, pe.ChangeType, Changes: pe.AttributeValueChanges.Count)).OrderBy(pe => pe.Id).ToList();
        Assert.That(pendingExportsBefore, Is.Not.Empty, "precondition: the first run provisioned into the target");
        var mvoValuesBefore = MvoValueSnapshot();

        var activity = await RunFullSyncAsync(ctx.Source);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ExecutionItems(activity).Where(r => r.ObjectChangeType != ObjectChangeType.NoChange
                        && r.ObjectChangeType != ObjectChangeType.NotSet).Select(r => r.ObjectChangeType),
                Is.Empty, "a repeat run over unchanged data records no changes");
            Assert.That(ExecutionItems(activity).Where(r => r.ErrorType is not (null or ActivityRunProfileExecutionItemErrorType.NotSet)),
                Is.Empty, "and no errors");
            Assert.That(MvoValueSnapshot(), Is.EqualTo(mvoValuesBefore), "the Metaverse is untouched");
            Assert.That(SyncRepo.PendingExports.Values
                    .Select(pe => (pe.Id, pe.ChangeType, Changes: pe.AttributeValueChanges.Count)).OrderBy(pe => pe.Id).ToList(),
                Is.EqualTo(pendingExportsBefore), "no Pending Export is staged, withdrawn or altered");
        }
    }

    private sealed record SourceContext(ConnectedSystem Source, int CsDisplayNameAttributeId, int MvDisplayNameAttributeId);

    /// <summary>
    /// One source system projecting a Person and flowing DisplayName and EmployeeId; optionally a relative-date scope
    /// on an EndDate attribute, and optionally a target system provisioned with DisplayName.
    /// </summary>
    private async Task<SourceContext> SetUpSourceAsync(DateTime? endDate = null, bool scopeToFutureEndDate = false, bool withTarget = false)
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
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var rule = await CreateImportSyncRuleAsync(source.Id, csoType, mvType, "HR Import");
        rule.AttributeFlowRules.Add(ImportMapping(rule, mvDisplayName, displayNameAttr));
        rule.AttributeFlowRules.Add(ImportMapping(rule, mvEmployeeId, employeeIdAttr));
        if (scopeToFutureEndDate)
        {
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
        }
        await DbContext.SaveChangesAsync();

        var cso = await CreateCsoAsync(source.Id, csoType, JohnSmith, "EMP001", created: DateTime.UtcNow.AddMinutes(-10));
        if (endDate.HasValue)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = endDateAttr.Id, Attribute = endDateAttr, DateTimeValue = endDate.Value, ConnectedSystemObject = cso
            });
        }

        if (withTarget)
        {
            var target = await CreateConnectedSystemAsync("Directory");
            var targetType = await CreateCsoTypeAsync(target.Id, "User");
            var exportRule = await CreateExportSyncRuleAsync(target.Id, targetType, mvType, "Directory Export");
            foreach (var (csName, mvAttribute) in new[] { ("DisplayName", mvDisplayName), ("EmployeeId", mvEmployeeId) })
            {
                var targetAttr = targetType.Attributes.Single(a => a.Name == csName);
                exportRule.AttributeFlowRules.Add(new SyncRuleMapping
                {
                    SyncRule = exportRule,
                    SyncRuleId = exportRule.Id,
                    TargetConnectedSystemAttribute = targetAttr,
                    TargetConnectedSystemAttributeId = targetAttr.Id,
                    Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvAttribute, MetaverseAttributeId = mvAttribute.Id } }
                });
            }
            await DbContext.SaveChangesAsync();
        }

        return new SourceContext(source, displayNameAttr.Id, mvDisplayName.Id);
    }

    private static SyncRuleMapping ImportMapping(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source) => new()
    {
        SyncRule = rule,
        SyncRuleId = rule.Id,
        TargetMetaverseAttribute = target,
        TargetMetaverseAttributeId = target.Id,
        Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
    };

    private List<(Guid MvoId, int AttributeId, string? Value)> MvoValueSnapshot() => SyncRepo.MetaverseObjects.Values
        .SelectMany(m => m.AttributeValues.Select(av => (m.Id, av.AttributeId, av.StringValue)))
        .OrderBy(v => v.Id).ThenBy(v => v.AttributeId).ThenBy(v => v.StringValue, StringComparer.Ordinal)
        .ToList();

    private List<ActivityRunProfileExecutionItem> ExecutionItems(Activity activity) => SyncRepo.Rpeis.Values
        .Where(r => r.ActivityId == activity.Id)
        .Concat(activity.RunProfileExecutionItems)
        .DistinctBy(r => r.Id)
        .ToList();

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"Full Sync {Guid.NewGuid():N}", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
