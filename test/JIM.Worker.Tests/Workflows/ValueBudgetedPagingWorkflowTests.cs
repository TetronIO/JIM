// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A synchronisation page ends at the page size or at a budget of attribute values, whichever comes first (#2934). Only the
/// page boundaries move: a Full or Delta Synchronisation paged by values must leave exactly the Metaverse, Pending Exports
/// and execution items that paging by object count alone leaves, processing every object once, in the same order.
/// </summary>
/// <remarks>
/// Each test runs the same directory twice, once by object count alone and once with a budget, on two fresh stores, and
/// compares what each left behind by name, since generated ids differ between runs. The directory is shaped so the budget
/// matters: people of two values, six to a page (the budget exactly met), then groups in import order with three large
/// ones together, one of them over the budget, and a nested group that shares the first large group's page by object count
/// but not by values, so one run resolves it in the page loop and the other in the cross-page reference step.
/// </remarks>
[TestFixture]
public class ValueBudgetedPagingWorkflowTests : WorkflowTestBase
{
    private const int PageSize = 6;
    private const int ValueBudget = 12;
    private const int People = 14;

    // Groups in import (id) order: two small, three large together, three small. Members are people by number; Large A also
    // holds Small C, the group imported just after the large ones.
    private static readonly (string Name, int[] People, string[] Groups)[] Groups =
    [
        ("Small A", [1], []),
        ("Small B", [2], []),
        ("Large A", [1, 2, 3, 4, 5, 6, 7, 8, 9], ["Small C"]),
        ("Large B", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], []),
        ("Large C", [3, 4, 5, 6, 7, 8], []),
        ("Small C", [12], []),
        ("Small D", [13], []),
        ("Small E", [14], [])
    ];

    [Test]
    public async Task FullSync_PagedByValues_LeavesTheSameOutcomeAsPagedByObjectCountAsync()
    {
        var unbudgeted = await RunFullAsync(int.MaxValue);
        BaseTearDown();
        BaseSetUp();
        var budgeted = await RunFullAsync(ValueBudget);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(budgeted.Outcome, Is.EqualTo(unbudgeted.Outcome), () => Difference(unbudgeted.Outcome, budgeted.Outcome));
            Assert.That(budgeted.Processed, Is.EqualTo(unbudgeted.Processed), "every object once, in the same order");
            Assert.That(budgeted.Processed, Is.Unique);
            Assert.That(budgeted.Processed, Has.Count.EqualTo(People + Groups.Length));

            Assert.That(unbudgeted.PageSizes, Is.EqualTo(new[] { 6, 6, 2, 6, 2 }), "by object count: three pages of people, two of groups");
            Assert.That(budgeted.PageSizes, Is.EqualTo(new[] { 6, 6, 2, 2, 1, 1, 2, 2 }),
                "by values: people keep the page size; the large groups no longer share a page, and the one over the budget is alone");
            Assert.That(budgeted.PagesWithinExpected, Is.True, "a page's expected total is never below its own number");
            Assert.That(budgeted.LastExpectedTotal, Is.EqualTo(budgeted.PageSizes.Count), "the last page knows it is the last");
            Assert.That(budgeted.CrossPageReferenceObjects, Is.EqualTo(1), "Large A's nested group is now on a later page, so it is deferred");
            Assert.That(unbudgeted.CrossPageReferenceObjects, Is.Zero);
        }
    }

    [Test]
    public async Task DeltaSync_PagedByValues_LeavesTheSameOutcomeAsPagedByObjectCountAsync()
    {
        var unbudgeted = await RunDeltaAsync(int.MaxValue);
        BaseTearDown();
        BaseSetUp();
        var budgeted = await RunDeltaAsync(ValueBudget);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(budgeted.Outcome, Is.EqualTo(unbudgeted.Outcome), () => Difference(unbudgeted.Outcome, budgeted.Outcome));
            Assert.That(budgeted.Processed, Is.EqualTo(unbudgeted.Processed), "every modified object once, in the same order");
            Assert.That(budgeted.Processed, Is.Unique);
            Assert.That(budgeted.Processed, Has.Count.EqualTo(6), "two people and four groups were modified");
            Assert.That(unbudgeted.PageSizes, Is.EqualTo(new[] { 2, 4 }));
            Assert.That(budgeted.PageSizes, Is.EqualTo(new[] { 2, 1, 1, 1, 1 }),
                "the two modified large groups each take a page, and neither shares one with a small group");
            Assert.That(budgeted.PagesWithinExpected, Is.True, "a page's expected total is never below its own number");
        }
    }

    private sealed record RunResult(List<string> Outcome, List<Guid> Processed, List<int> PageSizes, bool PagesWithinExpected,
        int LastExpectedTotal, int CrossPageReferenceObjects);

    /// <summary>What paging by values left out of, and added to, the outcome of paging by object count.</summary>
    private static string Difference(List<string> unbudgeted, List<string> budgeted) =>
        "paging by values changed the outcome. Only by object count:" + Environment.NewLine +
        string.Join(Environment.NewLine, unbudgeted.Except(budgeted)) + Environment.NewLine + "Only by values:" + Environment.NewLine +
        string.Join(Environment.NewLine, budgeted.Except(unbudgeted));

    private sealed record Directory(ConnectedSystem Source, ConnectedSystemObjectType UserType, ConnectedSystemObjectType GroupType,
        ConnectedSystemObjectTypeAttribute Member, Dictionary<string, ConnectedSystemObject> ByName);

    private async Task<RunResult> RunFullAsync(int valueBudget)
    {
        var directory = await BuildAsync();
        var (activity, run) = await SynchroniseAsync(directory.Source, ConnectedSystemRunType.FullSynchronisation, valueBudget);
        return run with { Outcome = Snapshot(activity) };
    }

    private async Task<RunResult> RunDeltaAsync(int valueBudget)
    {
        var directory = await BuildAsync();
        await SynchroniseAsync(directory.Source, ConnectedSystemRunType.FullSynchronisation, int.MaxValue);

        // After the Full Synchronisation: two people renamed, two large groups and two small ones changed.
        var modifiedAt = DateTime.UtcNow.AddSeconds(1);
        Rename(directory.ByName["Person 03"], "Person 03 (renamed)", modifiedAt);
        Rename(directory.ByName["Person 11"], "Person 11 (renamed)", modifiedAt);
        RemoveMember(directory, "Large A", "Person 01", modifiedAt);
        AddMember(directory, "Large B", "Person 12", modifiedAt);
        AddMember(directory, "Small B", "Person 13", modifiedAt);
        Rename(directory.ByName["Small D"], "Small D (renamed)", modifiedAt);

        var (activity, run) = await SynchroniseAsync(directory.Source, ConnectedSystemRunType.DeltaSynchronisation, valueBudget);
        return run with { Outcome = Snapshot(activity) };
    }

    private async Task<(Activity Activity, RunResult Run)> SynchroniseAsync(ConnectedSystem source, ConnectedSystemRunType runType, int valueBudget)
    {
        source = await ReloadEntityAsync(source);
        var profile = await CreateRunProfileAsync(source.Id, runType.ToString(), runType);
        var activity = await CreateActivityAsync(source.Id, profile, runType);

        var processed = new List<Guid>();
        var pageSizes = new List<int>();
        var withinExpected = true;
        var lastExpectedTotal = 0;
        var onPage = 0;

        SyncTaskProcessorBase processor = runType == ConnectedSystemRunType.FullSynchronisation
            ? new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, source, profile, activity, new CancellationTokenSource())
            : new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, source, profile, activity, new CancellationTokenSource());
        processor.PageValueBudgetForTests = valueBudget;
        processor.OnConnectedSystemObjectProcessedInPass2 = cso =>
        {
            processed.Add(cso.Id);
            onPage++;
        };
        processor.OnHistoryHandedOffForTests = batch =>
        {
            if (batch.Page == 0)
                return;
            pageSizes.Add(onPage);
            onPage = 0;
            withinExpected &= batch.TotalPages >= batch.Page;
            lastExpectedTotal = batch.TotalPages;
        };

        if (processor is SyncFullSyncTaskProcessor full)
            await full.PerformFullSyncAsync();
        else
            await ((SyncDeltaSyncTaskProcessor)processor).PerformDeltaSyncAsync();

        return (activity, new RunResult([], processed, pageSizes, withinExpected, lastExpectedTotal, processor.CrossPageReferenceObjectCount));
    }

    /// <summary>
    /// What the run left behind, by name: every Metaverse Object with its values, every target object with its status and
    /// Pending Export, and the run's execution items with their outcomes.
    /// </summary>
    private List<string> Snapshot(Activity activity)
    {
        string MvoName(MetaverseObject? mvo) =>
            mvo?.AttributeValues.FirstOrDefault(av => av.Attribute?.Name == "DisplayName")?.StringValue ?? "(none)";

        string MvoNameById(Guid? id) => id.HasValue && SyncRepo.MetaverseObjects.TryGetValue(id.Value, out var mvo) ? MvoName(mvo) : "(none)";

        string CsoName(Guid? id) => id.HasValue && SyncRepo.ConnectedSystemObjects.TryGetValue(id.Value, out var cso)
            ? $"{cso.ConnectedSystemId}:{MvoNameById(cso.MetaverseObjectId)}"
            : "(none)";

        string ReferenceText(string? value) => Guid.TryParse(value, out var id)
            ? SyncRepo.MetaverseObjects.ContainsKey(id) ? $"mvo {MvoNameById(id)}" : $"cso {CsoName(id)}"
            : value ?? "";

        var lines = new List<string>();
        foreach (var mvo in SyncRepo.MetaverseObjects.Values)
        {
            var values = mvo.AttributeValues
                .Select(av => $"{av.Attribute?.Name}={av.StringValue ?? (av.ReferenceValueId.HasValue ? MvoNameById(av.ReferenceValueId) : av.ReferenceValue != null ? MvoName(av.ReferenceValue) : "")}")
                .Order(StringComparer.Ordinal);
            lines.Add($"MVO {mvo.Type?.Name} {MvoName(mvo)}: {string.Join(", ", values)}");
        }

        foreach (var cso in SyncRepo.ConnectedSystemObjects.Values.Where(c => c.ConnectedSystemId != activity.ConnectedSystemId))
        {
            lines.Add($"Target {cso.Type?.Name} {MvoNameById(cso.MetaverseObjectId)}: {cso.Status}");
        }

        foreach (var pe in SyncRepo.PendingExports.Values)
        {
            var changes = pe.AttributeValueChanges
                .Select(c => $"{c.Attribute?.Name} {c.ChangeType} {c.StringValue ?? ReferenceText(c.UnresolvedReferenceValue)}{(c.ResolvedReferenceCsoId.HasValue ? " " + CsoName(c.ResolvedReferenceCsoId) : "")}")
                .Order(StringComparer.Ordinal);
            lines.Add($"PE {pe.ChangeType} {pe.Status} for {CsoName(pe.ConnectedSystemObjectId)}: {string.Join(", ", changes)}");
        }

        // The execution items as written, page by page, rather than those the Activity still holds in memory.
        static IEnumerable<string> Outcomes(IEnumerable<ActivityRunProfileExecutionItemSyncOutcome> outcomes, string parent) =>
            outcomes.SelectMany(o => Outcomes(o.Children, $"{parent}/{o.OutcomeType}").Prepend($"{parent}/{o.OutcomeType}"));

        foreach (var item in SyncRepo.Rpeis.Values.Where(r => r.ActivityId == activity.Id))
        {
            var outcomes = Outcomes(item.SyncOutcomes, "").Order(StringComparer.Ordinal);
            lines.Add($"RPEI {item.ObjectChangeType} {item.ErrorType} {CsoName(item.ConnectedSystemObjectId)}: {string.Join(", ", outcomes)}");
        }

        lines.Add($"Activity processed {activity.ObjectsProcessed} of {activity.ObjectsToProcess}");
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static void Rename(ConnectedSystemObject cso, string name, DateTime modifiedAt)
    {
        cso.AttributeValues.Single(av => av.Attribute.Name == "DisplayName").StringValue = name;
        cso.LastUpdated = modifiedAt;
    }

    private static void AddMember(Directory directory, string group, string member, DateTime modifiedAt)
    {
        var cso = directory.ByName[group];
        var target = directory.ByName[member];
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = directory.Member.Id, Attribute = directory.Member,
            ReferenceValueId = target.Id, ReferenceValue = target, ConnectedSystemObject = cso
        });
        cso.LastUpdated = modifiedAt;
    }

    private static void RemoveMember(Directory directory, string group, string member, DateTime modifiedAt)
    {
        var cso = directory.ByName[group];
        var target = directory.ByName[member];
        cso.AttributeValues.Remove(cso.AttributeValues.Single(av => av.AttributeId == directory.Member.Id && av.ReferenceValueId == target.Id));
        cso.LastUpdated = modifiedAt;
    }

    /// <summary>
    /// A source directory of people and groups (Member flowing to a multi-valued Metaverse reference) projecting into the
    /// Metaverse, and a target directory provisioning both, the group carrying DisplayName and Member.
    /// </summary>
    private async Task<Directory> BuildAsync()
    {
        var setting = DbContext.ServiceSettingItems.First(s => s.Key == Constants.SettingKeys.SyncPageSize);
        setting.Value = PageSize.ToString();
        await DbContext.SaveChangesAsync();

        var source = await CreateConnectedSystemAsync("Directory Source");
        var userType = await CreateCsoTypeAsync(source.Id, "User", [
            new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
        ]);
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "Member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        var groupType = await CreateCsoTypeAsync(source.Id, "Group", [
            new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            member
        ]);

        var mvPersonType = await CreateMvObjectTypeAsync("Person");
        var mvPersonDisplayName = mvPersonType.Attributes.First(a => a.Name == "DisplayName");
        var mvGroupType = await CreateMvObjectTypeAsync("Group");
        var mvGroupDisplayName = mvGroupType.Attributes.First(a => a.Name == "DisplayName");
        var mvMember = new MetaverseAttribute
        {
            Name = "Member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued,
            MetaverseObjectTypes = [mvGroupType], PredefinedSearchAttributes = []
        };
        DbContext.MetaverseAttributes.Add(mvMember);
        await DbContext.SaveChangesAsync();
        mvGroupType.Attributes.Add(mvMember);

        var userImport = await CreateImportSyncRuleAsync(source.Id, userType, mvPersonType, "User Import");
        userImport.AttributeFlowRules.Add(ImportMapping(userImport, mvPersonDisplayName, userType.Attributes.First(a => a.Name == "DisplayName")));
        var groupImport = await CreateImportSyncRuleAsync(source.Id, groupType, mvGroupType, "Group Import");
        groupImport.AttributeFlowRules.Add(ImportMapping(groupImport, mvGroupDisplayName, groupType.Attributes.First(a => a.Name == "DisplayName")));
        groupImport.AttributeFlowRules.Add(ImportMapping(groupImport, mvMember, member));
        await DbContext.SaveChangesAsync();

        var target = await CreateConnectedSystemAsync("Directory Target");
        var targetUserType = await CreateCsoTypeAsync(target.Id, "TargetUser", [
            new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
        ]);
        var targetMember = new ConnectedSystemObjectTypeAttribute
        {
            Name = "Member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        var targetGroupType = await CreateCsoTypeAsync(target.Id, "TargetGroup", [
            new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            targetMember
        ]);
        var userExport = await CreateExportSyncRuleAsync(target.Id, targetUserType, mvPersonType, "User Export");
        userExport.AttributeFlowRules.Add(ExportMapping(userExport, targetUserType.Attributes.First(a => a.Name == "DisplayName"), mvPersonDisplayName));
        var groupExport = await CreateExportSyncRuleAsync(target.Id, targetGroupType, mvGroupType, "Group Export");
        groupExport.AttributeFlowRules.Add(ExportMapping(groupExport, targetGroupType.Attributes.First(a => a.Name == "DisplayName"), mvGroupDisplayName));
        groupExport.AttributeFlowRules.Add(ExportMapping(groupExport, targetMember, mvMember));
        await DbContext.SaveChangesAsync();

        // Ids sort by number, so the import order above is the paging order.
        var byName = new Dictionary<string, ConnectedSystemObject>();
        for (var i = 1; i <= People; i++)
        {
            var name = $"Person {i:D2}";
            byName[name] = Seed(source.Id, userType, new Guid($"10000000-0000-0000-0000-{i:D12}"), name);
        }

        for (var i = 0; i < Groups.Length; i++)
            byName[Groups[i].Name] = Seed(source.Id, groupType, new Guid($"20000000-0000-0000-0000-{i + 1:D12}"), Groups[i].Name);

        var directory = new Directory(source, userType, groupType, member, byName);
        var created = DateTime.UtcNow.AddMinutes(-10);
        foreach (var (name, people, groups) in Groups)
        {
            foreach (var reference in people.Select(p => $"Person {p:D2}").Concat(groups))
                AddMember(directory, name, reference, created);
        }

        foreach (var cso in byName.Values)
            cso.LastUpdated = null;
        return directory;
    }

    private ConnectedSystemObject Seed(int connectedSystemId, ConnectedSystemObjectType type, Guid id, string displayName)
    {
        var cso = new ConnectedSystemObject
        {
            Id = id, ConnectedSystemId = connectedSystemId, TypeId = type.Id, Type = type,
            ConnectedSystem = SyncRepo.ConnectedSystems[connectedSystemId], Created = DateTime.UtcNow.AddMinutes(-10)
        };
        var externalId = type.Attributes.First(a => a.IsExternalId);
        var displayNameAttribute = type.Attributes.First(a => a.Name == "DisplayName");
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = externalId.Id, Attribute = externalId, GuidValue = id, ConnectedSystemObject = cso
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = displayNameAttribute.Id, Attribute = displayNameAttribute, StringValue = displayName, ConnectedSystemObject = cso
        });
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static SyncRuleMapping ImportMapping(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source) => new()
    {
        SyncRule = rule, SyncRuleId = rule.Id, TargetMetaverseAttribute = target, TargetMetaverseAttributeId = target.Id,
        Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
    };

    private static SyncRuleMapping ExportMapping(SyncRule rule, ConnectedSystemObjectTypeAttribute target, MetaverseAttribute source) => new()
    {
        SyncRule = rule, SyncRuleId = rule.Id, TargetConnectedSystemAttribute = target, TargetConnectedSystemAttributeId = target.Id,
        Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
    };
}
