// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Preview;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;
using Transition = JIM.Models.Activities.ActivityRunProfileExecutionItemSyncOutcomeType;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// The Full Synchronisation preview adapter (#1530): the rows a Full Synchronisation preview states, one per
/// consequence, read off the walk the fidelity tests above prove against the run. Each scenario pairs the adapter's
/// rows with what the run then does, so a row the run does not bear out fails here rather than in front of an
/// administrator deciding whether to run it.
/// </summary>
public partial class SyncPreviewFidelityTests
{
    [Test]
    public async Task FullSynchronisationAdapter_OfADriftedTarget_ProposesTheCorrectionByAttributeAsync()
    {
        var ctx = await SetUpDriftAsync(enforceState: true);
        var targetObject = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Target.Id);

        var deltas = await FullSynchronisationDeltasAsync(ctx.Target.Id);

        var correction = deltas.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Rows(deltas), Is.EqualTo(new[]
            {
                Row(Transition.DriftCorrection, "John Smith", ctx.Target.Id, "DisplayName", "Edited in AD", "John Smith")
            }));
            Assert.That(correction.ConnectedSystemObjectId, Is.EqualTo(targetObject.Id), "the object corrected");
            Assert.That(correction.MetaverseObjectId, Is.EqualTo(targetObject.MetaverseObjectId));
            Assert.That(correction.ObjectTypeName, Is.EqualTo("Person"));
        }
    }

    [Test]
    public async Task FullSynchronisationAdapter_OfAnObjectTheRunLeavesAsItIs_SaysItWouldNotChangeAsync()
    {
        var ctx = await SetUpDriftAsync(enforceState: true);

        var deltas = await FullSynchronisationDeltasAsync(ctx.Source.Id);

        Assert.That(Rows(deltas), Is.EqualTo(new[] { Row(Transition.WouldNotChange, "John Smith", ctx.Source.Id) }),
            "a source's synchronisation does not correct a target's drift, so the source object would not change");
    }

    [Test]
    public async Task FullSynchronisationAdapter_OfAnObjectTheRunSkipsAsUnchanged_SaysItWouldNotChangeAsync()
    {
        var ctx = await SetUpDriftAsync(enforceState: true);
        var targetObject = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Target.Id);
        targetObject.Created = DateTime.UtcNow.AddHours(-2);
        targetObject.LastUpdated = DateTime.UtcNow.AddHours(-1);
        var target = SyncRepo.ConnectedSystems[ctx.Target.Id];
        target.LastSyncCompletedAt = DateTime.UtcNow;
        target.ConfigurationLastFullyAppliedAt = DateTime.UtcNow;

        var deltas = await FullSynchronisationDeltasAsync(ctx.Target.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Rows(deltas), Is.EqualTo(new[] { Row(Transition.WouldNotChange, "Edited in AD", ctx.Target.Id) }),
                "skipped as unchanged, its drift and all");
            Assert.That(deltas.Single().ConnectedSystemObjectId, Is.EqualTo(targetObject.Id));
        }
    }

    [Test]
    public async Task FullSynchronisationAdapter_OfAnObjectLeavingScopeWhoseValuesAreRecalled_ProposesTheDisconnectionTheRecallAndItsExportAsync()
    {
        var ctx = await SetUpScopeExitWithRecallAsync();

        var deltas = await FullSynchronisationDeltasAsync(ctx.Source.Id);

        Assert.That(Rows(deltas), Is.EquivalentTo(new[]
        {
            Row(Transition.WouldDisconnectFromMetaverseObject, "John Smith", ctx.Source.Id),
            Row(Transition.NoContributor, "John Smith", null, Constants.BuiltInAttributes.DisplayName, "John Smith", null),
            Row(Transition.NoContributor, "John Smith", null, "EmployeeId", "EMP001", null),
            Row(Transition.WouldStageUpdateExport, "John Smith", ctx.Target.Id, "DisplayName", "John Smith", null)
        }));
    }

    [Test]
    public async Task FullSynchronisationAdapter_OfAnObsoleteObjectWhoseMetaverseObjectIsDeleted_ProposesTheTeardownAsync()
    {
        var ctx = await SetUpObsoleteSourceObjectAsync(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, adImports: false);
        var targetObject = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Target.Id);

        var deltas = await FullSynchronisationDeltasAsync(ctx.Source.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Rows(deltas), Is.EquivalentTo(new[]
            {
                Row(Transition.WouldDisconnectFromMetaverseObject, "John Smith", ctx.Source.Id),
                Row(Transition.WouldBecomeDeletionEligible, "John Smith", null, "Deletion eligibility", "Not eligible for deletion",
                    "Eligible for deletion immediately"),
                Row(Transition.WouldStageDeleteExport, "John Smith", ctx.Target.Id)
            }));
            Assert.That(deltas.Single(d => d.TransitionType == Transition.WouldStageDeleteExport).ConnectedSystemObjectId,
                Is.EqualTo(targetObject.Id), "the account deprovisioned");
        }
    }

    [Test]
    public async Task FullSynchronisationAdapter_OfAnObsoleteObjectWhoseMetaverseObjectIsScheduledForDeletion_SaysWhenAsync()
    {
        var ctx = await SetUpObsoleteSourceObjectAsync(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, adImports: false,
            gracePeriod: TimeSpan.FromDays(30));

        var deltas = await FullSynchronisationDeltasAsync(ctx.Source.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Select(d => d.TransitionType), Is.EquivalentTo(new[]
            {
                Transition.WouldDisconnectFromMetaverseObject, Transition.WouldBecomeDeletionEligible
            }), "a scheduled deletion deprovisions nothing yet");
            Assert.That(deltas.Single(d => d.TransitionType == Transition.WouldBecomeDeletionEligible).NewValue,
                Does.StartWith("Eligible for deletion after"));
        }
    }

    [Test]
    public async Task FullSynchronisationAdapter_OfANewObject_ProposesItsProjectionAndProvisioningAsync()
    {
        var ctx = await SetUpNewSourceObjectAsync();

        var deltas = await FullSynchronisationDeltasAsync(ctx.Source.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Rows(deltas), Is.EquivalentTo(new[]
            {
                Row(Transition.Projected, "John Smith", ctx.Source.Id),
                Row(Transition.Provisioned, "John Smith", ctx.Target.Id)
            }), "a projection's values are new, so they are not stated as changes");
            Assert.That(deltas.Single(d => d.TransitionType == Transition.Provisioned).ConnectedSystemObjectId,
                Is.EqualTo(ctx.SourceObject.Id), "the account does not exist yet, so the row names the object it is provisioned for");
        }
    }

    [Test]
    public async Task FullSynchronisationAdapter_OfATargetWithAMetaverseObjectFlaggedForReview_ProposesTheReviewsProvisioningAsync()
    {
        var ctx = await SetUpScopeReviewAsync();
        var john = SyncRepo.MetaverseObjects.Values.Single(m => m.ScopeReviewPending);

        var deltas = await FullSynchronisationDeltasAsync(ctx.Target.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Rows(deltas), Is.EqualTo(new[] { Row(Transition.Provisioned, "John Smith", ctx.Target.Id) }));
            Assert.That(deltas.Single().MetaverseObjectId, Is.EqualTo(john.Id));
        }
    }

    [Test]
    public async Task FullSynchronisationAdapter_OfAnObjectTheRunFails_ProposesTheFailureAloneAsync()
    {
        var source = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(source.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        var importRule = await CreateImportSyncRuleAsync(source.Id, sourceType, mvType, "HR Import");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule, TargetMetaverseAttribute = mvEmployeeId, TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = "cs[\"EmployeeId\"]", MissingInputBehaviour = JIM.Models.Expressions.MissingInputBehaviour.FailObject } }
        });
        await DbContext.SaveChangesAsync();
        await CreateCsoAsync(source.Id, sourceType, "John Smith", employeeId: null);

        var deltas = await FullSynchronisationDeltasAsync(source.Id);

        var failure = deltas.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.TransitionType, Is.EqualTo(Transition.WouldFailAttributeFlow));
            Assert.That(failure.ObjectDisplayName, Is.EqualTo("John Smith"));
            Assert.That(failure.AttributeName, Is.EqualTo("EmployeeId"));
            Assert.That(failure.NewValue, Is.Not.Empty, "the drill-down says why");
        }
    }

    [Test]
    public async Task FullSynchronisationAdapter_CountImpactAsync_CountsWhatTheRunDoesAsync()
    {
        // Two systems' worth of consequence at once: John Smith's teardown deletes him and deprovisions his account,
        // and Jane Doe projects and is provisioned.
        var ctx = await SetUpObsoleteSourceObjectAsync(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, adImports: false);
        var sourceType = SyncRepo.ConnectedSystemObjects.Values.First(c => c.ConnectedSystemId == ctx.Source.Id).Type;
        await CreateCsoAsync(ctx.Source.Id, sourceType, "Jane Doe", "EMP002");

        var counts = await FullSynchronisationAdapter.CountImpactAsync(FullSynchronisationContext(ctx.Source.Id));
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Source));

        var outcomes = activity.RunProfileExecutionItems.SelectMany(r => r.SyncOutcomes).ToList();
        int Recorded(params Transition[] types) => outcomes.Count(o => types.Contains(o.OutcomeType));
        int Counted(Transition transition) => counts.SingleOrDefault(c => c.TransitionType == transition)?.ObjectCount ?? 0;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Counted(Transition.Projected), Is.EqualTo(Recorded(Transition.Projected)).And.EqualTo(1));
            Assert.That(Counted(Transition.Provisioned), Is.EqualTo(CreatesStagedFor(ctx.Target)).And.EqualTo(1));
            Assert.That(Counted(Transition.WouldDisconnectFromMetaverseObject), Is.EqualTo(Recorded(Transition.Disconnected)).And.EqualTo(1));
            Assert.That(Counted(Transition.WouldBecomeDeletionEligible), Is.EqualTo(Recorded(Transition.MvoDeleted, Transition.MvoDeletionScheduled)));
            Assert.That(Counted(Transition.WouldStageDeleteExport), Is.EqualTo(DeletesStagedFor(ctx.Target)).And.EqualTo(1));
        }
    }

    [Test]
    public async Task FullSynchronisationAdapter_CreateImpactCounterAsync_FedItsOwnDeltas_CountsAsCountImpactAsyncDoesAsync()
    {
        var ctx = await SetUpObsoleteSourceObjectAsync(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, adImports: false);

        await JIM.Worker.Tests.Servers.PreviewImpactCounterEquivalence.AssertCountsFromItsOwnDeltasAsync(
            FullSynchronisationAdapter, FullSynchronisationContext(ctx.Source.Id));
    }

    [Test]
    public async Task FullSynchronisationAdapter_ProvisioningForTwoTargets_CountsEachTargetObjectAsync()
    {
        // One person provisioned to two systems is two accounts created, which is what "objects would be provisioned"
        // has to mean for the number to be the one an administrator expects in each system.
        var ctx = await SetUpNewSourceObjectAsync();
        var second = await CreateConnectedSystemAsync("Payroll Target");
        var secondType = await CreateCsoTypeAsync(second.Id, "employee");
        var mvType = SyncRepo.SyncRules.Values.Single(r => r.ConnectedSystemId == ctx.Target.Id).MetaverseObjectType!;
        var payrollExport = await CreateExportSyncRuleAsync(second.Id, secondType, mvType, "Payroll Export");
        FlowToAttribute(payrollExport, secondType.Attributes.First(a => a.Name == "DisplayName"),
            mvType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.DisplayName));
        await DbContext.SaveChangesAsync();

        var counts = await FullSynchronisationAdapter.CountImpactAsync(FullSynchronisationContext(ctx.Source.Id));

        Assert.That(counts.Single(c => c.TransitionType == Transition.Provisioned).ObjectCount, Is.EqualTo(2));
    }

    [Test]
    public void FullSynchronisationAdapter_ServesTheFullSynchronisationSurface()
    {
        var adapter = FullSynchronisationAdapter;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(adapter.Surface, Is.EqualTo(ConfigurationChangePreviewSurface.ConnectedSystemFullSynchronisation));
            Assert.That(adapter.ProducesDeltas, Is.True);
            Assert.That(adapter.ProposalType, Is.EqualTo(typeof(ConnectedSystemFullSynchronisationProposal)));
        }
    }

    [Test]
    public async Task FullSynchronisationAdapter_ValidateAsync_SystemWithAFullSynchronisationRunProfile_FindsNothingAsync()
    {
        var ctx = await SetUpNewSourceObjectAsync();
        await CreateRunProfileAsync(ctx.Source.Id, "Full Synchronisation", ConnectedSystemRunType.FullSynchronisation);

        var findings = await FullSynchronisationAdapter.ValidateAsync(FullSynchronisationContext(ctx.Source.Id));

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public async Task FullSynchronisationAdapter_ValidateAsync_SystemWithNoFullSynchronisationRunProfile_BlocksAsync()
    {
        // A preview of a run nobody can start answers nothing an administrator can act on.
        var ctx = await SetUpNewSourceObjectAsync();
        await CreateRunProfileAsync(ctx.Source.Id, "Delta Synchronisation", ConnectedSystemRunType.DeltaSynchronisation);

        var findings = await FullSynchronisationAdapter.ValidateAsync(FullSynchronisationContext(ctx.Source.Id));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings.Select(f => f.Severity), Is.EqualTo(new[] { PreviewValidationSeverity.Blocking }));
            Assert.That(findings.Single().Message, Does.Contain("Full Synchronisation Run Profile"));
        }
    }

    [Test]
    public async Task FullSynchronisationAdapter_ValidateAsync_SystemDoesNotExist_BlocksAsync()
    {
        var findings = await FullSynchronisationAdapter.ValidateAsync(FullSynchronisationContext(987_654));

        Assert.That(findings.Select(f => f.Severity), Is.EqualTo(new[] { PreviewValidationSeverity.Blocking }));
    }

    [Test]
    public async Task FullSynchronisationAdapter_ValidateAsync_SystemBeingDeleted_BlocksAsync()
    {
        var ctx = await SetUpNewSourceObjectAsync();
        await CreateRunProfileAsync(ctx.Source.Id, "Full Synchronisation", ConnectedSystemRunType.FullSynchronisation);
        DbContext.ChangeTracker.Clear();
        var persisted = await DbContext.ConnectedSystems.FindAsync(ctx.Source.Id);
        persisted!.Status = ConnectedSystemStatus.Deleting;
        await DbContext.SaveChangesAsync();

        var findings = await FullSynchronisationAdapter.ValidateAsync(FullSynchronisationContext(ctx.Source.Id));

        Assert.That(findings.Select(f => f.Severity), Is.EqualTo(new[] { PreviewValidationSeverity.Blocking }));
    }

    [Test]
    public async Task FullSynchronisationAdapter_ValidateAsync_EnabledDerivedFlowsFormACycle_BlocksAsync()
    {
        // The run refuses to start on a cycle and processes no object, so there is nothing to preview.
        var ctx = await SetUpDerivedAsync(emailExpression: "mv[\"User Principal Name\"]");
        await CreateRunProfileAsync(ctx.Hr.Id, "Full Synchronisation", ConnectedSystemRunType.FullSynchronisation);

        var findings = await FullSynchronisationAdapter.ValidateAsync(FullSynchronisationContext(ctx.Hr.Id));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings.Select(f => f.Severity), Is.EqualTo(new[] { PreviewValidationSeverity.Blocking }));
            Assert.That(findings.Single().Message, Does.Contain("cycle"));
        }
    }

    [Test]
    public async Task FullSynchronisationAdapter_ValidateAsync_CapBelowThePopulation_WarnsThatTheCountsDescribeOnlyThoseObjectsAsync()
    {
        var ctx = await SetUpNewSourceObjectAsync();
        await CreateCsoAsync(ctx.Source.Id, ctx.SourceObject.Type, "Jane Doe", "EMP002");
        await CreateRunProfileAsync(ctx.Source.Id, "Full Synchronisation", ConnectedSystemRunType.FullSynchronisation);

        var findings = await FullSynchronisationAdapter.ValidateAsync(FullSynchronisationContext(ctx.Source.Id, maxObjects: 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings.Select(f => f.Severity), Is.EqualTo(new[] { PreviewValidationSeverity.Warning }));
            Assert.That(findings.Single().Message, Does.Contain("1 of").And.Contain("2"));
        }
    }

    [Test]
    public async Task FullSynchronisationAdapter_ValidateAsync_CapThatEvaluatesNothing_BlocksAsync()
    {
        var ctx = await SetUpNewSourceObjectAsync();
        await CreateRunProfileAsync(ctx.Source.Id, "Full Synchronisation", ConnectedSystemRunType.FullSynchronisation);

        var findings = await FullSynchronisationAdapter.ValidateAsync(FullSynchronisationContext(ctx.Source.Id, maxObjects: 0));

        Assert.That(findings.Select(f => f.Severity), Is.EqualTo(new[] { PreviewValidationSeverity.Blocking }));
    }

    [Test]
    public async Task FullSynchronisationAdapter_EstimateCostAsync_IsTheSystemsObjectsAsync()
    {
        var ctx = await SetUpNewSourceObjectAsync();
        await CreateCsoAsync(ctx.Source.Id, ctx.SourceObject.Type, "Jane Doe", "EMP002");

        var estimate = await FullSynchronisationAdapter.EstimateCostAsync(FullSynchronisationContext(ctx.Source.Id));

        Assert.That(estimate.AffectedObjects, Is.EqualTo(2), "every object is evaluated, and each is at least its would-not-change row");
    }

    [Test]
    public async Task FullSynchronisationAdapter_EstimateCostAsync_EstimatesTheDurationFromThisSystemsLastCompletedRunAsync()
    {
        var ctx = await SetUpNewSourceObjectAsync();
        await CreateCsoAsync(ctx.Source.Id, ctx.SourceObject.Type, "Jane Doe", "EMP002");

        // This system's last completed Full Synchronisation evaluated 10 objects in 100 seconds: ten seconds each, far
        // slower than the reference. A newer failed run, another system's slower run and a Delta Synchronisation say
        // nothing about how long this system's Full Synchronisation takes.
        var executed = DateTime.UtcNow.AddHours(-2);
        DbContext.Activities.AddRange(
            RunActivity(ctx.Source.Id, ConnectedSystemRunType.FullSynchronisation, ActivityStatus.Complete, executed, objects: 10, seconds: 100),
            RunActivity(ctx.Source.Id, ConnectedSystemRunType.FullSynchronisation, ActivityStatus.FailedWithError, executed.AddMinutes(30), objects: 10, seconds: 1),
            RunActivity(ctx.Target.Id, ConnectedSystemRunType.FullSynchronisation, ActivityStatus.Complete, executed.AddMinutes(40), objects: 1, seconds: 1_000),
            RunActivity(ctx.Source.Id, ConnectedSystemRunType.DeltaSynchronisation, ActivityStatus.Complete, executed.AddMinutes(50), objects: 1, seconds: 1_000));
        await DbContext.SaveChangesAsync();

        var estimate = await FullSynchronisationAdapter.EstimateCostAsync(FullSynchronisationContext(ctx.Source.Id));

        Assert.That(estimate.EstimatedDuration, Is.EqualTo(TimeSpan.FromSeconds(20)), "two objects at ten seconds each");
    }

    [Test]
    public async Task FullSynchronisationAdapter_EstimateCostAsync_WithNoCompletedRun_EstimatesAtTheReferenceRateAsync()
    {
        var ctx = await SetUpNewSourceObjectAsync();

        var estimate = await FullSynchronisationAdapter.EstimateCostAsync(FullSynchronisationContext(ctx.Source.Id));

        Assert.That(estimate.EstimatedDuration,
            Is.EqualTo(TimeSpan.FromSeconds(1 / FullSynchronisationDurationEstimate.ReferenceObjectsPerSecond)));
    }

    [Test]
    public async Task FullSynchronisationAdapter_EvaluateDeltasAsync_WithACap_StopsAtItAsync()
    {
        var ctx = await SetUpNewSourceObjectAsync();
        await CreateCsoAsync(ctx.Source.Id, ctx.SourceObject.Type, "Jane Doe", "EMP002");

        var deltas = new List<PreviewDelta>();
        await foreach (var delta in FullSynchronisationAdapter.EvaluateDeltasAsync(FullSynchronisationContext(ctx.Source.Id, maxObjects: 1), CancellationToken.None))
            deltas.Add(delta);

        Assert.That(deltas.Count(d => d.TransitionType == Transition.Projected), Is.EqualTo(1), "one object evaluated, as the cap says");
    }

    [Test]
    public async Task FullSynchronisationAdapter_EvaluateDeltasAsync_Cancelled_StopsAsync()
    {
        var ctx = await SetUpNewSourceObjectAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.That(async () =>
        {
            await foreach (var _ in FullSynchronisationAdapter.EvaluateDeltasAsync(FullSynchronisationContext(ctx.Source.Id), cancellation.Token))
            {
            }
        }, Throws.InstanceOf<OperationCanceledException>());
    }

    private sealed record NewSourceObjectContext(ConnectedSystem Source, ConnectedSystem Target, ConnectedSystemObject SourceObject);

    /// <summary>
    /// HR holds John Smith, not yet synchronised, and Active Directory provisions everyone HR projects.
    /// </summary>
    private async Task<NewSourceObjectContext> SetUpNewSourceObjectAsync()
    {
        var source = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(source.Id, "User");
        var target = await CreateConnectedSystemAsync("AD Target");
        var targetType = await CreateCsoTypeAsync(target.Id, "user");
        var mvType = await CreateMvObjectTypeAsync("Person");
        mvType.Attributes.First(a => a.Name == "DisplayName").Name = Constants.BuiltInAttributes.DisplayName;
        await DbContext.SaveChangesAsync();

        await CreateImportSyncRuleWithDisplayNameFlowAsync(source, sourceType, mvType);
        var exportRule = await CreateExportSyncRuleAsync(target.Id, targetType, mvType, "AD Export");
        FlowToAttribute(exportRule, targetType.Attributes.First(a => a.Name == "DisplayName"),
            mvType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.DisplayName));
        await DbContext.SaveChangesAsync();
        var sourceObject = await CreateCsoAsync(source.Id, sourceType, "John Smith", "EMP001");

        return new NewSourceObjectContext(source, target, sourceObject);
    }

    private ConnectedSystemFullSynchronisationPreviewAdapter FullSynchronisationAdapter => new(Jim);

    private static Activity RunActivity(int connectedSystemId, ConnectedSystemRunType runType, ActivityStatus status, DateTime executed,
        int objects, int seconds) => new()
    {
        TargetType = ActivityTargetType.ConnectedSystemRunProfile,
        TargetOperationType = ActivityTargetOperationType.Execute,
        ConnectedSystemId = connectedSystemId,
        ConnectedSystemRunType = runType,
        Status = status,
        Executed = executed,
        ExecutionTime = TimeSpan.FromSeconds(seconds),
        ObjectsToProcess = objects
    };

    private static PreviewContext FullSynchronisationContext(int connectedSystemId, int? maxObjects = null) => new()
    {
        Surface = ConfigurationChangePreviewSurface.ConnectedSystemFullSynchronisation,
        ActivityId = Guid.NewGuid(),
        TargetId = connectedSystemId,
        ProposedConfiguration = new ConnectedSystemFullSynchronisationProposal(maxObjects)
    };

    private async Task<List<PreviewDelta>> FullSynchronisationDeltasAsync(int connectedSystemId)
    {
        var deltas = new List<PreviewDelta>();
        await foreach (var delta in FullSynchronisationAdapter.EvaluateDeltasAsync(FullSynchronisationContext(connectedSystemId), CancellationToken.None))
            deltas.Add(delta);
        return deltas;
    }

    private static (Transition Transition, string? Name, int? System, string? Attribute, string? OldValue, string? NewValue) Row(
        Transition transition, string? name, int? system, string? attribute = null, string? oldValue = null, string? newValue = null) =>
        (transition, name, system, attribute, oldValue, newValue);

    private static IEnumerable<(Transition Transition, string? Name, int? System, string? Attribute, string? OldValue, string? NewValue)> Rows(
        IEnumerable<PreviewDelta> deltas) =>
        deltas.Select(d => Row(d.TransitionType, d.ObjectDisplayName, d.ConnectedSystemId, d.AttributeName, d.OldValue, d.NewValue));
}
