// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// No-net-change detection withdraws a staged change that has become unnecessary. When export evaluation finds
/// an attribute already current on the target Connected System Object, any change for that attribute still
/// queued on the object's Pending Export (staged by an earlier synchronisation, and not yet exported) no longer
/// reflects the Metaverse and must not be exported: it is superseded by "no change", exactly as a newly evaluated
/// change for the attribute would supersede it (#1199). Before this, the early return for a fully no-net-change
/// evaluation left the queued change in place, so the next export overwrote a value the Metaverse agreed with.
/// Topology: an "HR" source projecting a Person (EmployeeId, DisplayName, and Title flowing to the Metaverse's
/// Type attribute), and a "Directory" target joined by EmployeeId whose export rule flows DisplayName and Type
/// back out, with no provisioning.
/// </summary>
[TestFixture]
public class StalePendingExportWithdrawalWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task FullSync_ValueChangesThenChangesBackBeforeExport_WithdrawsTheQueuedChangeAsync()
    {
        var ctx = await SetUpAsync(directoryProjects: false);
        var hrCso = SeedCso(ctx.Hr, ctx.HrType, "E1", "Alice", "Engineer");
        SeedCso(ctx.Directory, ctx.DirectoryType, "E1", "Alice", "Engineer");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Directory);
        Assert.That(DirectoryPendingExports(ctx), Is.Empty, "precondition: the joined account already matches");

        SetValue(hrCso, "DisplayName", "Alicia");
        await ModifyCsoAsync(hrCso);
        await RunFullSyncAsync(ctx.Hr);
        Assert.That(DirectoryChangeValues(ctx, "DisplayName"), Is.EqualTo(new[] { "Alicia" }), "precondition: the change is queued");

        SetValue(hrCso, "DisplayName", "Alice");
        await ModifyCsoAsync(hrCso);
        await RunFullSyncAsync(ctx.Hr);

        Assert.That(DirectoryPendingExports(ctx), Is.Empty,
            "the account already holds 'Alice' again, so the queued 'Alicia' must be withdrawn, not exported");
    }

    [Test]
    public async Task FullSync_OneOfTwoQueuedChangesBecomesUnnecessary_WithdrawsOnlyThatOneAsync()
    {
        var ctx = await SetUpAsync(directoryProjects: false);
        var hrCso = SeedCso(ctx.Hr, ctx.HrType, "E1", "Alice", "Engineer");
        SeedCso(ctx.Directory, ctx.DirectoryType, "E1", "Alice", "Engineer");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Directory);

        SetValue(hrCso, "DisplayName", "Alicia");
        SetValue(hrCso, "Title", "Architect");
        await ModifyCsoAsync(hrCso);
        await RunFullSyncAsync(ctx.Hr);

        SetValue(hrCso, "DisplayName", "Alice");
        await ModifyCsoAsync(hrCso);
        await RunFullSyncAsync(ctx.Hr);

        var pendingExport = DirectoryPendingExports(ctx).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pendingExport.AttributeValueChanges.Select(c => c.Attribute.Name), Is.EquivalentTo(new[] { "Title" }),
                "only the change the account no longer needs is withdrawn");
            Assert.That(pendingExport.AttributeValueChanges.Single().StringValue, Is.EqualTo("Architect"));
        }
    }

    [Test]
    public async Task FullSync_TargetSynchronisedFirstThenSourceSuppliesTheValueItHolds_WithdrawsTheQueuedClearAsync()
    {
        // Initialisation with the target synchronised before the source: the Directory projects the Person with
        // no DisplayName yet, so its export flow queues a clear of the account's DisplayName. HR then joins and
        // supplies "Alice", which the account already holds: the queued clear must be withdrawn rather than
        // blanking the account on the next export.
        var ctx = await SetUpAsync(directoryProjects: true);
        SeedCso(ctx.Directory, ctx.DirectoryType, "E1", "Alice", "Engineer");
        await RunFullSyncAsync(ctx.Directory);
        Assert.That(DirectoryPendingExports(ctx), Is.Not.Empty, "precondition: the Directory's own run queued clears");

        SeedCso(ctx.Hr, ctx.HrType, "E1", "Alice", "Engineer");
        await RunFullSyncAsync(ctx.Hr);

        Assert.That(DirectoryPendingExports(ctx), Is.Empty,
            "the Metaverse now agrees with every value the account holds, so nothing is left to export");
    }

    [Test]
    public async Task FullSync_ValueChangesThenChangesBackBeforeExport_RecordsTheWithdrawalOnTheObjectsItemAsync()
    {
        // A withdrawal changes what the next export will do to the target, so the run must say so on the item of the
        // object whose change caused it, not only in the service log (#2001).
        var ctx = await SetUpAsync(directoryProjects: false);
        var hrCso = SeedCso(ctx.Hr, ctx.HrType, "E1", "Alice", "Engineer");
        var account = SeedCso(ctx.Directory, ctx.DirectoryType, "E1", "Alice", "Engineer");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Directory);
        SetValue(hrCso, "DisplayName", "Alicia");
        await ModifyCsoAsync(hrCso);
        await RunFullSyncAsync(ctx.Hr);

        SetValue(hrCso, "DisplayName", "Alice");
        await ModifyCsoAsync(hrCso);
        var activity = await RunFullSyncAsync(ctx.Hr);

        var withdrawal = WithdrawalOutcomes(activity).SingleOrDefault();
        Assert.That(withdrawal, Is.Not.Null, "the withdrawal is recorded on the object's item: " + DescribeOutcomes(activity));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(withdrawal!.TargetEntityId, Is.EqualTo(account.Id), "naming the account whose queued change was withdrawn");
            Assert.That(withdrawal!.TargetEntityDescription, Is.EqualTo("Directory"), "and the system it is in");
            Assert.That(withdrawal!.DetailCount, Is.EqualTo(1), "counting the changes withdrawn");
            Assert.That(withdrawal!.SyncRuleName, Is.EqualTo("Directory Export"), "attributed to the export rule that queued them");
            Assert.That(withdrawal!.ParentSyncOutcome?.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow),
                "nested under the Attribute Flow that made the change unnecessary, as a staged export is");
            Assert.That(SnapshotValues(withdrawal!), Is.EquivalentTo(new[] { "DisplayName=Alicia" }), "showing what was withdrawn");
            Assert.That(DirectoryPendingExports(ctx), Is.Empty, "arrange check: the queued change was withdrawn");
        }
    }

    [Test]
    public async Task FullSync_OneQueuedChangeBecomesUnnecessaryWhileAnotherIsStaged_RecordsOnlyTheWithdrawnOneAsync()
    {
        // The merge path: a new Title is staged in the same evaluation that finds DisplayName already current. The
        // queued Title is superseded by the new one, which is not a withdrawal; only the queued DisplayName is.
        var ctx = await SetUpAsync(directoryProjects: false);
        var hrCso = SeedCso(ctx.Hr, ctx.HrType, "E1", "Alice", "Engineer");
        var account = SeedCso(ctx.Directory, ctx.DirectoryType, "E1", "Alice", "Engineer");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Directory);
        SetValue(hrCso, "DisplayName", "Alicia");
        SetValue(hrCso, "Title", "Architect");
        await ModifyCsoAsync(hrCso);
        await RunFullSyncAsync(ctx.Hr);

        SetValue(hrCso, "DisplayName", "Alice");
        SetValue(hrCso, "Title", "Director");
        await ModifyCsoAsync(hrCso);
        var activity = await RunFullSyncAsync(ctx.Hr);

        var withdrawal = WithdrawalOutcomes(activity).SingleOrDefault();
        Assert.That(withdrawal, Is.Not.Null, "the withdrawal is recorded on the object's item: " + DescribeOutcomes(activity));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(withdrawal!.TargetEntityId, Is.EqualTo(account.Id));
            Assert.That(withdrawal!.DetailCount, Is.EqualTo(1), "the superseded Title is replaced, not withdrawn");
            Assert.That(SnapshotValues(withdrawal!), Is.EquivalentTo(new[] { "DisplayName=Alicia" }));
            Assert.That(DirectoryChangeValues(ctx, "Title"), Is.EqualTo(new[] { "Director" }), "arrange check: the new Title is staged");
            Assert.That(DirectoryChangeValues(ctx, "DisplayName"), Is.Empty, "arrange check: the queued DisplayName was withdrawn");
        }
    }

    [Test]
    public async Task FullSync_WithdrawalAtStandardTracking_IsRecordedAsARootOutcomeAsync()
    {
        // The processor reads the level from the service settings, not the repository.
        DbContext.ServiceSettingItems.Add(new ServiceSetting
        {
            Key = Constants.SettingKeys.ChangeTrackingSyncOutcomesLevel,
            DisplayName = "Sync outcome tracking level",
            Category = ServiceSettingCategory.History,
            ValueType = ServiceSettingValueType.Enum,
            DefaultValue = nameof(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Standard)
        });
        await DbContext.SaveChangesAsync();
        var ctx = await SetUpAsync(directoryProjects: false);
        var hrCso = SeedCso(ctx.Hr, ctx.HrType, "E1", "Alice", "Engineer");
        SeedCso(ctx.Directory, ctx.DirectoryType, "E1", "Alice", "Engineer");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Directory);
        SetValue(hrCso, "DisplayName", "Alicia");
        await ModifyCsoAsync(hrCso);
        await RunFullSyncAsync(ctx.Hr);

        SetValue(hrCso, "DisplayName", "Alice");
        await ModifyCsoAsync(hrCso);
        var activity = await RunFullSyncAsync(ctx.Hr);

        var withdrawal = WithdrawalOutcomes(activity).SingleOrDefault();
        Assert.That(withdrawal, Is.Not.Null, DescribeOutcomes(activity));
        Assert.That(withdrawal!.ParentSyncOutcome, Is.Null, "Standard tracking records no children");
    }

    [Test]
    public async Task FullSync_NothingQueued_RecordsNoWithdrawalAsync()
    {
        // The account already holds the new value and nothing was ever queued for it, so there is nothing to withdraw.
        var ctx = await SetUpAsync(directoryProjects: false);
        var hrCso = SeedCso(ctx.Hr, ctx.HrType, "E1", "Alice", "Engineer");
        var account = SeedCso(ctx.Directory, ctx.DirectoryType, "E1", "Alice", "Engineer");
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Directory);
        SetValue(account, "DisplayName", "Alicia");

        SetValue(hrCso, "DisplayName", "Alicia");
        await ModifyCsoAsync(hrCso);
        var activity = await RunFullSyncAsync(ctx.Hr);

        Assert.That(WithdrawalOutcomes(activity), Is.Empty, DescribeOutcomes(activity));
    }

    [Test]
    public async Task FullSync_MergedClassAttributeWithdrawnBeforeExport_WithdrawsTheClassItObligedAsync()
    {
        // The first flow of a merged auxiliary class's attribute stages the class add with it. When the attribute's
        // value is then withdrawn before the export, the target already holds what the Metaverse wants (nothing), so the
        // queued attribute change is withdrawn. The class add was only there because of it: left behind, it asks the
        // directory to add a class whose required attributes nothing writes, so every export is refused (Scenario 019,
        // Dora).
        var ctx = await SetUpAsync(directoryProjects: false, titleIsMergedClassAttribute: true);
        var hrCso = SeedCso(ctx.Hr, ctx.HrType, "E1", "Alice", "Engineer");
        SeedCso(ctx.Directory, ctx.DirectoryType, "E1", "Alice", title: null);
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Directory);
        Assert.That(DirectoryChangeValues(ctx, "objectClass"), Is.EqualTo(new[] { "badgeHolder" }),
            "precondition: the first flow of the merged class's attribute stages the class with it");

        RemoveValue(hrCso, "Title");
        await ModifyCsoAsync(hrCso);
        await RunFullSyncAsync(ctx.Hr);

        Assert.That(DirectoryPendingExports(ctx), Is.Empty,
            "with the attribute withdrawn, nothing obliges the class, so nothing is left to export");
    }

    [Test]
    public async Task FullSync_MergedClassAttributeWithdrawnWhileAnotherOfItsAttributesIsQueued_KeepsTheClassAsync()
    {
        // The class stays while any queued change still carries one of its attributes.
        var ctx = await SetUpAsync(directoryProjects: false, titleIsMergedClassAttribute: true, displayNameIsMergedClassAttribute: true);
        var hrCso = SeedCso(ctx.Hr, ctx.HrType, "E1", "Alice", "Engineer");
        SeedCso(ctx.Directory, ctx.DirectoryType, "E1", "Alice", title: null);
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Directory);
        SetValue(hrCso, "DisplayName", "Alicia");
        await ModifyCsoAsync(hrCso);
        await RunFullSyncAsync(ctx.Hr);

        RemoveValue(hrCso, "Title");
        await ModifyCsoAsync(hrCso);
        await RunFullSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DirectoryChangeValues(ctx, "Title"), Is.Empty, "arrange check: the queued Title was withdrawn");
            Assert.That(DirectoryChangeValues(ctx, "DisplayName"), Is.EqualTo(new[] { "Alicia" }), "the other attribute's change stays queued");
            Assert.That(DirectoryChangeValues(ctx, "objectClass"), Is.EqualTo(new[] { "badgeHolder" }),
                "and it still obliges the class, so the class add stays with it");
        }
    }

    // ---- Topology ----

    private sealed record Context(
        ConnectedSystem Hr,
        ConnectedSystemObjectType HrType,
        ConnectedSystem Directory,
        ConnectedSystemObjectType DirectoryType);

    private async Task<Context> SetUpAsync(bool directoryProjects, bool titleIsMergedClassAttribute = false, bool displayNameIsMergedClassAttribute = false)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvTitle = mvType.Attributes.First(a => a.Name == "Type");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User", Attributes());
        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "User", Attributes(withClassMembership: titleIsMergedClassAttribute));
        if (titleIsMergedClassAttribute)
            await MergeAuxiliaryClassAsync(directoryType, displayNameIsMergedClassAttribute ? ["Title", "DisplayName"] : ["Title"]);

        // HR: projects (or, when the Directory projects, joins by EmployeeId) and contributes every value.
        var hrImport = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import", enableProjection: !directoryProjects);
        AddImportFlow(hrImport, hrType, "EmployeeId", mvEmployeeId);
        AddImportFlow(hrImport, hrType, "DisplayName", mvDisplayName);
        AddImportFlow(hrImport, hrType, "Title", mvTitle);
        if (directoryProjects)
            AddMatchingRule(hrImport, hrType, mvEmployeeId);

        // Directory: joins by EmployeeId (or projects, for the target-first ordering) and contributes only the
        // EmployeeId it is joined by; its export rule flows DisplayName and Title back out.
        var directoryImport = await CreateImportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Import", enableProjection: directoryProjects);
        AddImportFlow(directoryImport, directoryType, "EmployeeId", mvEmployeeId);
        if (!directoryProjects)
            AddMatchingRule(directoryImport, directoryType, mvEmployeeId);

        var directoryExport = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Export", enableProvisioning: false);
        AddExportFlow(directoryExport, directoryType, mvDisplayName, "DisplayName");
        AddExportFlow(directoryExport, directoryType, mvTitle, "Title");

        await DbContext.SaveChangesAsync();
        return new Context(hr, hrType, directory, directoryType);
    }

    private static List<ConnectedSystemObjectTypeAttribute> Attributes(bool withClassMembership = false)
    {
        List<ConnectedSystemObjectTypeAttribute> attributes =
        [
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "Title", Type = AttributeDataType.Text, Selected = true }
        ];
        if (withClassMembership)
            attributes.Add(new() { Name = "objectClass", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued, Selected = true });
        return attributes;
    }

    /// <summary>
    /// Makes the Directory compose objectClass, with <paramref name="attributeNames"/> contributed by a merged
    /// "badgeHolder" auxiliary class, as an RFC 4512 directory's schema does once an auxiliary class is merged.
    /// </summary>
    private async Task MergeAuxiliaryClassAsync(ConnectedSystemObjectType directoryType, IEnumerable<string> attributeNames)
    {
        var badgeHolder = await CreateCsoTypeAsync(directoryType.ConnectedSystemId, "badgeHolder", []);
        badgeHolder.Selected = false;
        directoryType.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassMembershipAttribute, Value = "objectClass" });
        directoryType.Extensions.Add(new ConnectedSystemObjectTypeExtension
        {
            BaseObjectTypeId = directoryType.Id,
            ExtensionObjectTypeId = badgeHolder.Id,
            ExtensionObjectType = badgeHolder
        });
        foreach (var name in attributeNames)
            directoryType.Attributes.Single(a => a.Name == name).ClassName = "badgeHolder";
        await DbContext.SaveChangesAsync();
    }

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

    private static void AddExportFlow(SyncRule rule, ConnectedSystemObjectType csoType, MetaverseAttribute source, string csAttributeName)
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

    private static void AddMatchingRule(SyncRule rule, ConnectedSystemObjectType csoType, MetaverseAttribute mvEmployeeId)
    {
        var source = csoType.Attributes.Single(a => a.Name == "EmployeeId");
        rule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeId,
            TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        });
    }

    private ConnectedSystemObject SeedCso(ConnectedSystem system, ConnectedSystemObjectType type, string employeeId, string displayName, string? title)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            TypeId = type.Id,
            Type = type,
            ConnectedSystem = SyncRepo.ConnectedSystems[system.Id],
            Created = DateTime.UtcNow
        };
        AddValue(cso, type, "ExternalId", guidValue: Guid.NewGuid());
        AddValue(cso, type, "EmployeeId", employeeId);
        AddValue(cso, type, "DisplayName", displayName);
        if (title != null)
            AddValue(cso, type, "Title", title);
        if (type.Attributes.Any(a => a.Name == "objectClass"))
            AddValue(cso, type, "objectClass", type.Name);
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static void AddValue(ConnectedSystemObject cso, ConnectedSystemObjectType type, string name, string? stringValue = null, Guid? guidValue = null)
    {
        var attribute = type.Attributes.Single(a => a.Name == name);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = attribute.Id, Attribute = attribute,
            StringValue = stringValue, GuidValue = guidValue
        });
    }

    private static void SetValue(ConnectedSystemObject cso, string name, string value) =>
        cso.AttributeValues.Single(av => av.Attribute.Name == name).StringValue = value;

    private static void RemoveValue(ConnectedSystemObject cso, string name) =>
        cso.AttributeValues.Remove(cso.AttributeValues.Single(av => av.Attribute.Name == name));

    private List<PendingExport> DirectoryPendingExports(Context ctx) =>
        SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == ctx.Directory.Id).ToList();

    private IReadOnlyList<string?> DirectoryChangeValues(Context ctx, string attributeName) =>
        DirectoryPendingExports(ctx)
            .SelectMany(pe => pe.AttributeValueChanges)
            .Where(c => c.Attribute.Name == attributeName)
            .Select(c => c.StringValue)
            .ToList();

    private static List<ActivityRunProfileExecutionItemSyncOutcome> WithdrawalOutcomes(Activity activity) =>
        activity.RunProfileExecutionItems
            .SelectMany(item => item.SyncOutcomes)
            .Where(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportChangesWithdrawn)
            .ToList();

    private static List<string> SnapshotValues(ActivityRunProfileExecutionItemSyncOutcome outcome) =>
        outcome.ConnectedSystemObjectChange?.AttributeChanges
            .SelectMany(a => a.ValueChanges.Select(v => $"{a.Attribute?.Name}={v.StringValue}"))
            .ToList() ?? [];

    private static string DescribeOutcomes(Activity activity) =>
        string.Join("; ", activity.RunProfileExecutionItems.Select(item =>
            string.Join(", ", item.SyncOutcomes.Select(o => o.OutcomeType.ToString()))));

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
