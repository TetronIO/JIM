// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Every run that staged a change into a Pending Export can be found from that Pending Export (#2100, Phase 3-0).
/// </summary>
/// <remarks>
/// The Pending Export lists the runs that staged it, and the Causality Explorer's Queued by panel reads that list.
/// Each staging run's own outcome names the Pending Export by id too, which is how Phase 2's downward walk finds the
/// export still waiting. A later run merging its change into an export still queued replaces it, so the replacement
/// must keep both: the runs, and (while nothing has been sent) the id. A run whose change is still in the queue but
/// cannot be found from it is a run whose consequences read as finished when they are not.
/// </remarks>
[TestFixture]
public class PendingExportStagerWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task FullSync_StagesAnExport_ItsItemIsFoundFromThePendingExportAsync()
    {
        var ctx = await SetUpAsync();

        ChangeHrValue(ctx, "Alice", "DisplayName", "Alice Smith");
        var run = await RunFullSyncAsync(ctx.Hr);

        var export = QueuedExportFor(ctx, "Alice");
        Assert.That(StagersOf(export), Is.EqualTo(new[] { HrItemOf(ctx, run, "Alice").Id }));
    }

    [Test]
    public async Task FullSync_RestagesAnExportQueuedByAnEarlierRun_BothRunsAreFoundFromThePendingExportAsync()
    {
        // The second run finds the first run's export still queued for the same account and merges its own change
        // into it. Both changes now wait in one Pending Export, so both runs staged it.
        var ctx = await SetUpAsync();

        ChangeHrValue(ctx, "Alice", "DisplayName", "Alice Smith");
        var first = await RunFullSyncAsync(ctx.Hr);
        ChangeHrValue(ctx, "Alice", "Title", "Manager");
        var second = await RunFullSyncAsync(ctx.Hr);

        var export = QueuedExportFor(ctx, "Alice");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(export.AttributeValueChanges.Select(c => c.Attribute?.Name), Is.EquivalentTo(new[] { "DisplayName", "Title" }),
                "arrange: the second run's change was merged with the first's");
            Assert.That(StagersOf(export), Is.EqualTo(new[] { HrItemOf(ctx, first, "Alice").Id, HrItemOf(ctx, second, "Alice").Id }));
        }
    }

    [Test]
    public async Task FullSync_RestagesAnExportQueuedByAnEarlierRun_TheEarlierRunsOutcomeStillNamesAQueuedExportAsync()
    {
        // The earlier run's own record must lead somewhere real: its outcome is how its After this run section finds
        // the export still waiting.
        var ctx = await SetUpAsync();

        ChangeHrValue(ctx, "Alice", "DisplayName", "Alice Smith");
        var first = await RunFullSyncAsync(ctx.Hr);
        ChangeHrValue(ctx, "Alice", "Title", "Manager");
        await RunFullSyncAsync(ctx.Hr);

        var namedExportIds = PendingExportOutcomesOf(HrItemOf(ctx, first, "Alice")).Select(o => o.TargetEntityId).ToList();
        Assert.That(namedExportIds, Has.Some.EqualTo(QueuedExportFor(ctx, "Alice").Id));
    }

    [Test]
    public async Task FullSync_RestagesAnExportQueuedByAnEarlierRun_TheReplacementKeepsItsIdAsync()
    {
        var ctx = await SetUpAsync();

        ChangeHrValue(ctx, "Alice", "DisplayName", "Alice Smith");
        await RunFullSyncAsync(ctx.Hr);
        var queued = QueuedExportFor(ctx, "Alice").Id;
        ChangeHrValue(ctx, "Alice", "Title", "Manager");
        await RunFullSyncAsync(ctx.Hr);

        Assert.That(QueuedExportFor(ctx, "Alice").Id, Is.EqualTo(queued));
    }

    [Test]
    public async Task FullSync_RestagesAnExportAlreadySent_TheReplacementIsANewExportThatStillListsBothRunsAsync()
    {
        // Sent and awaiting confirmation, the original is an export cycle of its own: its id stays with what was
        // sent, so the replacement takes a new one. The first run's change is still in the replacement, though, so
        // its run is still one of the replacement's stagers.
        var ctx = await SetUpAsync();

        ChangeHrValue(ctx, "Alice", "DisplayName", "Alice Smith");
        var first = await RunFullSyncAsync(ctx.Hr);
        var sent = QueuedExportFor(ctx, "Alice");
        sent.Status = PendingExportStatus.Exported;
        sent.LastAttemptedAt = DateTime.UtcNow;
        ChangeHrValue(ctx, "Alice", "Title", "Manager");
        var second = await RunFullSyncAsync(ctx.Hr);

        var replacement = QueuedExportFor(ctx, "Alice");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(replacement.Id, Is.Not.EqualTo(sent.Id));
            Assert.That(StagersOf(replacement), Is.EqualTo(new[] { HrItemOf(ctx, first, "Alice").Id, HrItemOf(ctx, second, "Alice").Id }));
        }
    }

    [Test]
    public async Task FullSync_ObjectLeavesTheExportRulesScope_ItsItemIsFoundFromTheDeleteAsync()
    {
        var ctx = await SetUpAsync(scopedToEmployees: true, deprovisionAction: OutboundDeprovisionAction.Delete);

        ChangeHrValue(ctx, "Carol", "Title", "Contractor");
        var run = await RunFullSyncAsync(ctx.Hr);

        var delete = QueuedExportFor(ctx, "Carol");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(delete.ChangeType, Is.EqualTo(PendingExportChangeType.Delete), "arrange: Carol left the rule's scope");
            Assert.That(StagersOf(delete), Is.EqualTo(new[] { HrItemOf(ctx, run, "Carol").Id }));
        }
    }

    // ─── Arrangement ───

    private sealed record Context(
        ConnectedSystem Hr,
        ConnectedSystem Directory,
        ConnectedSystemObjectType HrType,
        ConnectedSystemObjectType DirectoryType);

    /// <summary>
    /// HR projects three people; the Directory holds a joined, current account for each, and an export rule keeps
    /// their DisplayName and Title in step. Nothing is queued when this returns.
    /// </summary>
    private async Task<Context> SetUpAsync(bool scopedToEmployees = false,
        OutboundDeprovisionAction deprovisionAction = OutboundDeprovisionAction.Disconnect)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvTitle = mvType.Attributes.First(a => a.Name == "Type");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User", Attributes());
        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "User", Attributes());

        var hrImport = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        AddImportFlow(hrImport, hrType, "EmployeeId", mvEmployeeId);
        AddImportFlow(hrImport, hrType, "DisplayName", mvDisplayName);
        AddImportFlow(hrImport, hrType, "Title", mvTitle);

        var directoryImport = await CreateImportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Import", enableProjection: false);
        AddImportFlow(directoryImport, directoryType, "EmployeeId", mvEmployeeId);
        var directoryEmployeeId = directoryType.Attributes.Single(a => a.Name == "EmployeeId");
        directoryImport.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = directoryImport,
            SyncRuleId = directoryImport.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeId,
            TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = directoryEmployeeId, ConnectedSystemAttributeId = directoryEmployeeId.Id } }
        });

        var exportRule = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Export",
            enableProvisioning: false, deprovisionAction: deprovisionAction);
        if (scopedToEmployees)
        {
            exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
            {
                Type = SearchGroupType.All,
                Criteria = new List<SyncRuleScopingCriteria>
                {
                    new() { MetaverseAttribute = mvTitle, MetaverseAttributeId = mvTitle.Id, ComparisonType = SearchComparisonType.Equals, StringValue = "Employee", CaseSensitive = true }
                }
            });
        }
        AddExportFlow(exportRule, directoryType, "DisplayName", mvDisplayName);
        AddExportFlow(exportRule, directoryType, "Title", mvTitle);
        await DbContext.SaveChangesAsync();

        foreach (var (employeeId, name) in new[] { ("E1", "Alice"), ("E2", "Bob"), ("E3", "Carol") })
        {
            SeedCso(hr, hrType, employeeId, name, "Employee");
            SeedCso(directory, directoryType, employeeId, name, "Employee");
        }

        await RunFullSyncAsync(hr);
        await RunFullSyncAsync(directory);
        Assert.That(SyncRepo.PendingExports.Values, Is.Empty, "arrange: every joined account is current");

        return new Context(hr, directory, hrType, directoryType);
    }

    private static List<ConnectedSystemObjectTypeAttribute> Attributes() =>
    [
        new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
        new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "Title", Type = AttributeDataType.Text, Selected = true }
    ];

    private static void AddImportFlow(SyncRule rule, ConnectedSystemObjectType csoType, string csAttributeName, MetaverseAttribute target)
    {
        var source = csoType.Attributes.Single(a => a.Name == csAttributeName);
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        });
    }

    private static void AddExportFlow(SyncRule rule, ConnectedSystemObjectType csoType, string csAttributeName, MetaverseAttribute source)
    {
        var target = csoType.Attributes.Single(a => a.Name == csAttributeName);
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
        });
    }

    private void SeedCso(ConnectedSystem system, ConnectedSystemObjectType type, string employeeId, string displayName, string title)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            TypeId = type.Id,
            Type = type,
            ConnectedSystem = SyncRepo.ConnectedSystems[system.Id],
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        };
        var externalId = type.Attributes.Single(a => a.IsExternalId);
        cso.ExternalIdAttributeId = externalId.Id;
        AddValue(cso, externalId, guidValue: Guid.NewGuid());
        AddValue(cso, type.Attributes.Single(a => a.Name == "EmployeeId"), employeeId);
        AddValue(cso, type.Attributes.Single(a => a.Name == "DisplayName"), displayName);
        AddValue(cso, type.Attributes.Single(a => a.Name == "Title"), title);
        SyncRepo.SeedConnectedSystemObject(cso);
    }

    private static void AddValue(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute, string? stringValue = null, Guid? guidValue = null) =>
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = attribute.Id, Attribute = attribute,
            StringValue = stringValue, GuidValue = guidValue
        });

    /// <summary>Changes one of a person's values in HR, found by the EmployeeId-independent DisplayName they were seeded with.</summary>
    private void ChangeHrValue(Context ctx, string seededName, string attributeName, string newValue)
    {
        var cso = HrCsoOf(ctx, seededName);
        cso.AttributeValues.Single(av => av.Attribute?.Name == attributeName).StringValue = newValue;
        cso.LastUpdated = DateTime.UtcNow;
    }

    private ConnectedSystemObject HrCsoOf(Context ctx, string seededName)
    {
        var employeeId = seededName switch { "Alice" => "E1", "Bob" => "E2", "Carol" => "E3", _ => throw new ArgumentOutOfRangeException(nameof(seededName)) };
        return SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Hr.Id &&
            c.AttributeValues.Any(av => av.Attribute?.Name == "EmployeeId" && av.StringValue == employeeId));
    }

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    // ─── The lookup Queued by performs ───

    /// <summary>The one Pending Export queued for a person's Directory account.</summary>
    private PendingExport QueuedExportFor(Context ctx, string seededName)
    {
        var mvoId = HrCsoOf(ctx, seededName).MetaverseObjectId;
        var account = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Directory.Id && c.MetaverseObjectId == mvoId);
        return SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == account.Id);
    }

    /// <summary>The item a run recorded for a person's HR record.</summary>
    private ActivityRunProfileExecutionItem HrItemOf(Context ctx, Activity run, string seededName)
    {
        var csoId = HrCsoOf(ctx, seededName).Id;
        return SyncRepo.Rpeis.Values.Single(i => i.ActivityId == run.Id && i.ConnectedSystemObjectId == csoId);
    }

    /// <summary>The runs a Pending Export records as having staged it, oldest first: what Queued by lists.</summary>
    private static IReadOnlyList<Guid> StagersOf(PendingExport export) => export.Stagers();

    private static IEnumerable<ActivityRunProfileExecutionItemSyncOutcome> PendingExportOutcomesOf(ActivityRunProfileExecutionItem item) =>
        Flatten(item.SyncOutcomes).Where(o => SyncOutcomeTypes.IsPendingExport(o.OutcomeType));

    private static IEnumerable<ActivityRunProfileExecutionItemSyncOutcome> Flatten(IEnumerable<ActivityRunProfileExecutionItemSyncOutcome> outcomes) =>
        outcomes.SelectMany(o => new[] { o }.Concat(Flatten(o.Children))).Distinct();
}
