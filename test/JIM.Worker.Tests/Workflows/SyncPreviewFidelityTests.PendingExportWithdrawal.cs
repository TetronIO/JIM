// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using NUnit.Framework;
using Transition = JIM.Models.Activities.ActivityRunProfileExecutionItemSyncOutcomeType;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Changes queued on a target's Pending Export that a synchronisation withdraws, because the target already holds the
/// values the Metaverse now wants (#2001). The run records the withdrawal on the object's item, so both previews must
/// propose it too: an administrator deciding whether to run needs to see that queued exports would disappear.
/// Topology: an "HR" source projecting a Person (EmployeeId, DisplayName, and Title flowing to the Metaverse's Type
/// attribute), and a "Directory" target joined by EmployeeId whose export rule flows DisplayName and Type back out.
/// </summary>
public partial class SyncPreviewFidelityTests
{
    [Test]
    public async Task PreviewSyncForCsoAsync_ValueChangedBackBeforeTheQueuedChangeWasExported_ProposesTheWithdrawalTheRunMakesAsync()
    {
        var ctx = await SetUpQueuedChangeAsync();
        SetWithdrawalValue(ctx.HrCso, "DisplayName", "Alice");
        await ModifyCsoAsync(ctx.HrCso);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.HrCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));

        var described = DescribeTree(preview.OutcomeTree);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(described, Does.Contain(nameof(Transition.PendingExportChangesWithdrawn)), "the preview proposes the withdrawal");
            Assert.That(described, Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_OneQueuedChangeWithdrawnWhileAnotherIsStaged_ProposesBothAsTheRunMakesThemAsync()
    {
        var ctx = await SetUpQueuedChangeAsync(alsoQueueTitle: true);
        SetWithdrawalValue(ctx.HrCso, "DisplayName", "Alice");
        SetWithdrawalValue(ctx.HrCso, "Title", "Director");
        await ModifyCsoAsync(ctx.HrCso);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.HrCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));

        var described = DescribeTree(preview.OutcomeTree);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(described, Does.Contain(nameof(Transition.PendingExportChangesWithdrawn) + " (count: 1"),
                "only the queued DisplayName is withdrawn; the queued Title is replaced");
            Assert.That(described, Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_NothingQueued_ProposesNoWithdrawalAsync()
    {
        var ctx = await SetUpQueuedChangeAsync(queueDisplayName: false);
        SetWithdrawalValue(ctx.DirectoryCso, "DisplayName", "Alicia");
        SetWithdrawalValue(ctx.HrCso, "DisplayName", "Alicia");
        await ModifyCsoAsync(ctx.HrCso);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.HrCso.Id);

        Assert.That(DescribeTree(preview.OutcomeTree), Does.Not.Contain(nameof(Transition.PendingExportChangesWithdrawn)),
            "the account already holds the new value and nothing is queued for it, so nothing is withdrawn");
    }

    [Test]
    public async Task FullSynchronisationAdapter_OfAQueuedChangeTheTargetNoLongerNeeds_ProposesTheWithdrawalByAttributeAsync()
    {
        var ctx = await SetUpQueuedChangeAsync();
        SetWithdrawalValue(ctx.HrCso, "DisplayName", "Alice");
        await ModifyCsoAsync(ctx.HrCso);

        var deltas = await FullSynchronisationDeltasAsync(ctx.Hr.Id);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));

        var withdrawal = deltas.SingleOrDefault(d => d.TransitionType == Transition.PendingExportChangesWithdrawn);
        Assert.That(withdrawal, Is.Not.Null, "the preview states the withdrawal: " + string.Join("; ", deltas.Select(d => d.TransitionType)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(withdrawal!.ConnectedSystemId, Is.EqualTo(ctx.Directory.Id), "in the system whose queue loses the change");
            Assert.That(withdrawal!.ConnectedSystemObjectId, Is.EqualTo(ctx.DirectoryCso.Id), "for the account the change was queued for");
            Assert.That(withdrawal!.AttributeName, Is.EqualTo("DisplayName"));
            Assert.That(withdrawal!.OldValue, Is.EqualTo("Alicia"), "what was queued");
            Assert.That(withdrawal!.NewValue, Is.EqualTo("Alice"), "what the account keeps");
            Assert.That(deltas.Select(d => d.TransitionType), Has.None.EqualTo(Transition.WouldStageUpdateExport),
                "nothing new is staged");
            Assert.That(SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == ctx.Directory.Id), Is.Empty,
                "arrange check: the run withdrew it");
        }
    }

    [Test]
    public async Task FullSynchronisationAdapter_OfAQueuedChangeWithdrawnWhileAnotherIsStaged_ProposesEachByAttributeAsync()
    {
        var ctx = await SetUpQueuedChangeAsync(alsoQueueTitle: true);
        SetWithdrawalValue(ctx.HrCso, "DisplayName", "Alice");
        SetWithdrawalValue(ctx.HrCso, "Title", "Director");
        await ModifyCsoAsync(ctx.HrCso);

        var deltas = await FullSynchronisationDeltasAsync(ctx.Hr.Id);

        var outbound = deltas
            .Where(d => d.TransitionType is Transition.PendingExportChangesWithdrawn or Transition.WouldStageUpdateExport)
            .Select(d => (d.TransitionType, d.AttributeName, d.OldValue, d.NewValue));
        Assert.That(outbound, Is.EquivalentTo(new[]
        {
            (Transition.PendingExportChangesWithdrawn, "DisplayName", "Alicia", "Alice"),
            (Transition.WouldStageUpdateExport, "Title", "Engineer", "Director")
        }), "the queued Title is replaced by the new one, not withdrawn");
    }

    // ---- Topology ----

    private sealed record QueuedChangeContext(
        ConnectedSystem Hr,
        ConnectedSystemObject HrCso,
        ConnectedSystem Directory,
        ConnectedSystemObject DirectoryCso);

    /// <summary>
    /// Synchronises Alice into the joined Directory account (both holding "Alice", "Engineer"), then, unless asked not
    /// to, changes her HR DisplayName to "Alicia" (and her Title to "Architect" when asked) and synchronises again, so
    /// the Directory's Pending Export queues those changes, unexported.
    /// </summary>
    private async Task<QueuedChangeContext> SetUpQueuedChangeAsync(bool alsoQueueTitle = false, bool queueDisplayName = true)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvTitle = mvType.Attributes.First(a => a.Name == "Type");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User", WithdrawalAttributes());
        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "User", WithdrawalAttributes());

        var hrImport = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import", enableProjection: true);
        AddWithdrawalImportFlow(hrImport, hrType, "EmployeeId", mvEmployeeId);
        AddWithdrawalImportFlow(hrImport, hrType, "DisplayName", mvDisplayName);
        AddWithdrawalImportFlow(hrImport, hrType, "Title", mvTitle);

        var directoryImport = await CreateImportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Import", enableProjection: false);
        AddWithdrawalImportFlow(directoryImport, directoryType, "EmployeeId", mvEmployeeId);
        var employeeIdSource = directoryType.Attributes.Single(a => a.Name == "EmployeeId");
        directoryImport.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = directoryImport,
            SyncRuleId = directoryImport.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeId,
            TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = employeeIdSource, ConnectedSystemAttributeId = employeeIdSource.Id } }
        });

        var directoryExport = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Export", enableProvisioning: false);
        AddWithdrawalExportFlow(directoryExport, directoryType, mvDisplayName, "DisplayName");
        AddWithdrawalExportFlow(directoryExport, directoryType, mvTitle, "Title");
        await DbContext.SaveChangesAsync();

        var hrCso = SeedWithdrawalCso(hr, hrType, "E1", "Alice", "Engineer");
        var directoryCso = SeedWithdrawalCso(directory, directoryType, "E1", "Alice", "Engineer");
        await RunFullSyncAsync(await ReloadEntityAsync(hr));
        await RunFullSyncAsync(await ReloadEntityAsync(directory));

        if (!queueDisplayName)
            return new QueuedChangeContext(hr, hrCso, directory, directoryCso);

        SetWithdrawalValue(hrCso, "DisplayName", "Alicia");
        if (alsoQueueTitle)
            SetWithdrawalValue(hrCso, "Title", "Architect");
        await ModifyCsoAsync(hrCso);
        await RunFullSyncAsync(await ReloadEntityAsync(hr));
        Assert.That(SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == directory.Id).SelectMany(pe => pe.AttributeValueChanges).ToList(),
            Has.Count.EqualTo(alsoQueueTitle ? 2 : 1), "arrange check: the changes are queued");

        return new QueuedChangeContext(hr, hrCso, directory, directoryCso);
    }

    private static List<ConnectedSystemObjectTypeAttribute> WithdrawalAttributes() =>
    [
        new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
        new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "Title", Type = AttributeDataType.Text, Selected = true }
    ];

    private static void AddWithdrawalImportFlow(SyncRule rule, ConnectedSystemObjectType csoType, string csAttributeName, MetaverseAttribute target)
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

    private static void AddWithdrawalExportFlow(SyncRule rule, ConnectedSystemObjectType csoType, MetaverseAttribute source, string csAttributeName)
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

    private ConnectedSystemObject SeedWithdrawalCso(ConnectedSystem system, ConnectedSystemObjectType type, string employeeId, string displayName, string title)
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
        var values = new (string Name, string? StringValue, Guid? GuidValue)[]
        {
            ("ExternalId", null, Guid.NewGuid()), ("EmployeeId", employeeId, null), ("DisplayName", displayName, null), ("Title", title, null)
        };
        cso.AttributeValues.AddRange(values.Select(value =>
        {
            var attribute = type.Attributes.Single(a => a.Name == value.Name);
            return new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = attribute.Id, Attribute = attribute,
                StringValue = value.StringValue, GuidValue = value.GuidValue
            };
        }));
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static void SetWithdrawalValue(ConnectedSystemObject cso, string name, string value) =>
        cso.AttributeValues.Single(av => av.Attribute.Name == name).StringValue = value;
}
