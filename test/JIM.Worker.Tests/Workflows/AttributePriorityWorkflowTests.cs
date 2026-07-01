// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// End-to-end workflow tests for attribute priority resolution (#91), driving the real Full Sync pipeline
/// (processors, engine, repositories) against an in-memory database rather than the engine in isolation.
///
/// Topology: two import sources both contribute DisplayName to the same Person MVO, at distinct priorities.
/// A Directory source projects the MVO first; an HR source then joins it (matching on EmployeeId) and also
/// contributes DisplayName. The highest-priority contribution must win regardless of which source syncs first,
/// proving the inline incumbent-comparison gate engages once the worker supplies a priority context.
/// </summary>
[TestFixture]
public class AttributePriorityWorkflowTests : WorkflowTestBase
{
    private const string DirectoryDisplayName = "Directory Display";
    private const string HrDisplayName = "HR Display";
    private const string SharedEmployeeId = "EMP001";

    [Test]
    public async Task FullSync_HigherPriorityLaterContributor_WinsOverProjectedIncumbentAsync()
    {
        // Directory (priority 2) projects first; HR (priority 1) joins second and must overwrite DisplayName.
        var ctx = await SetUpTwoSourcesAsync(directoryDisplayNamePriority: 2, hrDisplayNamePriority: 1);

        await RunFullSyncAsync(ctx.Directory);
        await RunFullSyncAsync(ctx.Hr);

        var displayName = ResolvedDisplayName(ctx);
        Assert.That(displayName, Is.EqualTo(HrDisplayName),
            "the higher-priority HR contribution must win even though it syncs second");
    }

    [Test]
    public async Task FullSync_LowerPriorityLaterContributor_DoesNotOverwriteHigherPriorityIncumbentAsync()
    {
        // Directory (priority 1) projects first; HR (priority 2) joins second and must NOT overwrite DisplayName.
        var ctx = await SetUpTwoSourcesAsync(directoryDisplayNamePriority: 1, hrDisplayNamePriority: 2);

        await RunFullSyncAsync(ctx.Directory);
        await RunFullSyncAsync(ctx.Hr);

        var displayName = ResolvedDisplayName(ctx);
        Assert.That(displayName, Is.EqualTo(DirectoryDisplayName),
            "the lower-priority HR contribution must not overwrite the higher-priority Directory value");
    }

    [Test]
    public async Task FullSync_HigherPriorityAssertsNull_ClearsLowerPriorityValueAndBlocksResurrectionAsync()
    {
        // HR (priority 1, "Null is a value") contributes no DisplayName: it asserts null over Directory's (priority 2)
        // value, and the persisted asserted-null marker blocks the lower-priority Directory value from resurrecting on
        // a later sync (the "clears must propagate" guarantee).
        var ctx = await SetUpTwoSourcesAsync(
            directoryDisplayNamePriority: 2,
            hrDisplayNamePriority: 1,
            hrNullIsValue: true,
            hrContributesDisplayName: false);

        await RunFullSyncAsync(ctx.Directory); // projects MVO, DisplayName = Directory Display
        await RunFullSyncAsync(ctx.Hr);         // HR (higher priority) asserts null

        Assert.That(ResolvedDisplayName(ctx), Is.Null, "the higher-priority null assertion clears the Directory value");
        AssertAssertedNullMarker(ctx);

        // Directory syncs again: it must still lose to the asserted-null marker (no resurrection).
        await RunFullSyncAsync(ctx.Directory);

        Assert.That(ResolvedDisplayName(ctx), Is.Null, "the lower-priority Directory value must not resurrect over the asserted null");
        AssertAssertedNullMarker(ctx);
    }

    [Test]
    public async Task FullSync_AssertsNull_EmitsAssertedNullSyncOutcomeAsync()
    {
        // HR (priority 1, "Null is a value") asserts null over Directory's (priority 2) DisplayName. The assertion
        // must surface in the RPEI outcome graph as an AssertedNull outcome, so an admin can see the blank was
        // positively asserted, not merely "no contributor".
        var ctx = await SetUpTwoSourcesAsync(
            directoryDisplayNamePriority: 2,
            hrDisplayNamePriority: 1,
            hrNullIsValue: true,
            hrContributesDisplayName: false);

        await RunFullSyncAsync(ctx.Directory);                            // projects MVO, DisplayName = Directory Display
        var hrActivity = await RunFullSyncReturningActivityAsync(ctx.Hr);  // HR (higher priority) asserts null

        Assert.That(ResolvedDisplayName(ctx), Is.Null, "precondition: HR's assertion clears the Directory value");

        var assertedNullOutcomes = hrActivity.RunProfileExecutionItems
            .SelectMany(r => r.SyncOutcomes)
            .Where(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull)
            .ToList();
        Assert.That(assertedNullOutcomes, Is.Not.Empty,
            "an asserted-null contribution should emit an AssertedNull sync outcome in the RPEI graph");
        Assert.That(assertedNullOutcomes.Sum(o => o.DetailCount ?? 0), Is.GreaterThanOrEqualTo(1),
            "the AssertedNull outcome should count the asserted attribute(s)");
    }

    /// <summary>
    /// Asserts the one Person MVO holds exactly one asserted-null DisplayName marker (carrying HR's provenance) and no
    /// real DisplayName value.
    /// </summary>
    private void AssertAssertedNullMarker(TwoSourceContext ctx)
    {
        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var displayNameRows = mvo.AttributeValues.Where(av => av.AttributeId == ctx.MvDisplayNameAttributeId).ToList();

        Assert.That(displayNameRows.Any(av => !av.NullValue), Is.False, "no real DisplayName value should remain");
        var markers = displayNameRows.Where(av => av.NullValue).ToList();
        Assert.That(markers, Has.Count.EqualTo(1), "exactly one asserted-null marker should be persisted");
        Assert.That(markers[0].ContributedBySyncRuleId, Is.EqualTo(ctx.HrImportRuleId),
            "the marker must carry the asserting HR rule's provenance");
    }

    /// <summary>
    /// Holds the entities a priority workflow test needs to run the syncs and read the resolved MVO value.
    /// </summary>
    private sealed record TwoSourceContext(
        ConnectedSystem Directory,
        ConnectedSystem Hr,
        int MvDisplayNameAttributeId,
        int HrImportRuleId);

    /// <summary>
    /// Builds the two-source topology: a Directory source that projects the Person MVO and contributes DisplayName
    /// (and EmployeeId, the match key) at <paramref name="directoryDisplayNamePriority"/>, and an HR source that
    /// joins on EmployeeId and contributes DisplayName at <paramref name="hrDisplayNamePriority"/>. Both source CSOs
    /// share an EmployeeId so the HR CSO joins the Directory-projected MVO.
    /// </summary>
    private async Task<TwoSourceContext> SetUpTwoSourcesAsync(
        int directoryDisplayNamePriority,
        int hrDisplayNamePriority,
        bool hrNullIsValue = false,
        bool hrContributesDisplayName = true)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        // --- Directory source: projects the MVO, contributes DisplayName + EmployeeId ---
        var directorySystem = await CreateConnectedSystemAsync("Corporate Directory");
        var directoryType = await CreateCsoTypeAsync(directorySystem.Id, "DirectoryUser");
        var directoryDisplayNameAttr = directoryType.Attributes.Single(a => a.Name == "DisplayName");
        var directoryEmployeeIdAttr = directoryType.Attributes.Single(a => a.Name == "EmployeeId");

        var directoryImportRule = await CreateImportSyncRuleAsync(directorySystem.Id, directoryType, mvType, "Directory Import");
        directoryImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = directoryImportRule,
            SyncRuleId = directoryImportRule.Id,
            Priority = directoryDisplayNamePriority,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = directoryDisplayNameAttr,
                ConnectedSystemAttributeId = directoryDisplayNameAttr.Id
            }}
        });
        directoryImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = directoryImportRule,
            SyncRuleId = directoryImportRule.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = directoryEmployeeIdAttr,
                ConnectedSystemAttributeId = directoryEmployeeIdAttr.Id
            }}
        });
        await DbContext.SaveChangesAsync();

        // --- HR source: joins on EmployeeId, contributes DisplayName ---
        var hrSystem = await CreateConnectedSystemAsync("HR System");
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser");
        var hrDisplayNameAttr = hrType.Attributes.Single(a => a.Name == "DisplayName");
        var hrEmployeeIdAttr = hrType.Attributes.Single(a => a.Name == "EmployeeId");

        var hrImportRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import", enableProjection: false);
        hrImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImportRule,
            SyncRuleId = hrImportRule.Id,
            Priority = hrDisplayNamePriority,
            NullIsValue = hrNullIsValue,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = hrDisplayNameAttr,
                ConnectedSystemAttributeId = hrDisplayNameAttr.Id
            }}
        });
        // Join rule: match on EmployeeId (CaseSensitive for in-memory DB; ILike is PostgreSQL-specific).
        hrImportRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = hrImportRule,
            SyncRuleId = hrImportRule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Order = 0,
                    ConnectedSystemAttribute = hrEmployeeIdAttr,
                    ConnectedSystemAttributeId = hrEmployeeIdAttr.Id
                }
            }
        });
        await DbContext.SaveChangesAsync();

        // Both source CSOs share an EmployeeId so the HR CSO joins the Directory-projected MVO.
        await CreateCsoAsync(directorySystem.Id, directoryType, DirectoryDisplayName, SharedEmployeeId);
        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, HrDisplayName, SharedEmployeeId);

        if (!hrContributesDisplayName)
        {
            // Remove HR's DisplayName value so HR contributes ConnectedNoValue for DisplayName (asserting null when
            // NullIsValue is set), while still joining on EmployeeId.
            var hrDisplayNameValue = hrCso.AttributeValues.Single(av => av.AttributeId == hrDisplayNameAttr.Id);
            hrCso.AttributeValues.Remove(hrDisplayNameValue);
        }

        return new TwoSourceContext(directorySystem, hrSystem, mvDisplayNameAttr.Id, hrImportRule.Id);
    }

    /// <summary>
    /// Runs a Full Sync for the given Connected System through the real processor.
    /// </summary>
    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        await RunFullSyncReturningActivityAsync(connectedSystem);
    }

    /// <summary>
    /// Runs a Full Sync for the given Connected System through the real processor, returning the Activity so the
    /// caller can inspect its Run Profile Execution Items and sync outcomes.
    /// </summary>
    private async Task<Activity> RunFullSyncReturningActivityAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    /// <summary>
    /// Reads the single resolved DisplayName value from the one Person MVO (excluding any asserted-null markers).
    /// </summary>
    private string? ResolvedDisplayName(TwoSourceContext ctx)
    {
        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var value = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDisplayNameAttributeId && !av.NullValue);
        return value?.StringValue;
    }
}
