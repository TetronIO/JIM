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
/// The object type an export outcome records for its target, in the "csId|csoTypeName" channel the causality panel
/// reads to name the object ("user: Alice Smith"). It must be the target object's own type. A Connected System that
/// exports more than one type (users and groups to one directory) used to record every object as whichever type its
/// first export Synchronisation Rule targeted, so a provisioned person read "group: Alice Smith".
/// </summary>
[TestFixture]
public class ExportOutcomeObjectTypeTests : WorkflowTestBase
{
    [Test]
    public async Task PerformFullSyncAsync_TargetExportsGroupsBeforeUsers_ProvisionedPersonIsRecordedAsAUserAsync()
    {
        // Arrange
        var (sourceSystem, targetSystem) = await ArrangeTargetExportingGroupsThenUsersAsync();

        // Act
        var activity = await RunFullSyncAsync(sourceSystem);

        // Assert
        var outcomes = activity.RunProfileExecutionItems.SelectMany(r => r.SyncOutcomes).ToList();
        var provisioned = outcomes.Single(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned);
        var pendingExport = outcomes.Single(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(provisioned.DetailMessage, Is.EqualTo($"{targetSystem.Id}|user"),
                "The provisioned object is a user; the target's first export Synchronisation Rule targets groups");
            Assert.That(pendingExport.DetailMessage, Is.EqualTo($"{targetSystem.Id}|user"),
                "The Pending Export staged for the provisioned user names the user's type too");
        }
    }

    #region Arrange

    /// <summary>
    /// An HR system projecting people, and one directory with two export Synchronisation Rules: groups, created
    /// first so that it is the first rule the directory's exports are looked up by, then users. Only a person
    /// exists, so the only object provisioned is a user.
    /// </summary>
    private async Task<(ConnectedSystem SourceSystem, ConnectedSystem TargetSystem)> ArrangeTargetExportingGroupsThenUsersAsync()
    {
        var targetSystem = await CreateConnectedSystemAsync("Directory");
        var groupMvType = await CreateMvObjectTypeAsync("Group");
        var targetGroupType = await CreateCsoTypeAsync(targetSystem.Id, "group");
        await CreateExportSyncRuleAsync(targetSystem.Id, targetGroupType, groupMvType, "Directory Export Groups");

        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var personMvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = personMvType.Attributes.First(a => a.Name == "DisplayName");

        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, personMvType, "HR Import");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = sourceType.Attributes.First(a => a.Name == "DisplayName"),
                ConnectedSystemAttributeId = sourceType.Attributes.First(a => a.Name == "DisplayName").Id
            }}
        });

        var targetUserType = await CreateCsoTypeAsync(targetSystem.Id, "user");
        var exportUsersRule = await CreateExportSyncRuleAsync(targetSystem.Id, targetUserType, personMvType, "Directory Export Users");
        var targetDisplayNameAttr = targetUserType.Attributes.First(a => a.Name == "DisplayName");
        exportUsersRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportUsersRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                MetaverseAttribute = mvDisplayNameAttr,
                MetaverseAttributeId = mvDisplayNameAttr.Id
            }}
        });

        await CreateCsoAsync(sourceSystem.Id, sourceType, "Alice Smith", "EMP001");
        return (sourceSystem, targetSystem);
    }

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem sourceSystem)
    {
        var profile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(sourceSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    #endregion
}
