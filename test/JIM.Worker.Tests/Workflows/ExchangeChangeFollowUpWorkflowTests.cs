// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exchange;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.TestSupport;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// An Exchange change is a follow-up of the export item that wrote the account (#2021, #2668), driven through the real
/// synchronisation and export pipelines. The export server names the item before it is written, so the link only holds
/// if the item that is written takes that name. Topology: an "HR" source projecting a Person, and a "Directory" target
/// whose export rule provisions a <c>user</c> and flows Exchange Recipient Type as Mailbox.
/// </summary>
[TestFixture]
public class ExchangeChangeFollowUpWorkflowTests : WorkflowTestBase
{
    private const string DomainController = "dc01.corp.example";

    [Test]
    public async Task Export_ProvisioningAMailbox_QueuesAChangeWhoseActivityFollowsUpTheItemTheRunWroteAsync()
    {
        var (hr, directory) = await SetUpAsync();
        await RunFullSyncAsync(hr);

        var export = await RunExportAsync(directory);

        var items = SyncRepo.Rpeis.Values.Where(i => i.ActivityId == export.Id).ToList();
        var staged = SyncRepo.PendingExchangeChanges.Values.Single();
        var activity = SyncRepo.Activities[staged.ActivityId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(items, Has.Count.EqualTo(1), "arrange: the export provisioned one account");
            Assert.That(staged.ConnectedSystemObjectId, Is.EqualTo(items.Single().ConnectedSystemObjectId));
            Assert.That(staged.RecipientType, Is.EqualTo(ExchangeRecipientTypes.Mailbox));
            Assert.That(staged.DomainController, Is.EqualTo(DomainController), "the domain controller the write went to");
            Assert.That(activity.TargetType, Is.EqualTo(ActivityTargetType.ExchangeRecipient));
            Assert.That(activity.ParentRunProfileExecutionItemId, Is.EqualTo(items.Single().Id),
                "the Activity points at the item the run actually wrote");
        }
    }

    private async Task<(ConnectedSystem Hr, ConnectedSystem Directory)> SetUpAsync()
    {
        // Tests run as though the feature had shipped (test/CLAUDE.md); the harness reads Service Settings from its database.
        DbContext.ServiceSettingItems.AddRange(await InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled().GetAllSettingsAsync());

        var mvType = await CreateMvObjectTypeAsync("Person");
        var displayName = mvType.Attributes.First(a => a.Name == "DisplayName");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "HrUser");
        var importRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule, SyncRuleId = importRule.Id,
            TargetMetaverseAttribute = displayName, TargetMetaverseAttributeId = displayName.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrType.Attributes.Single(a => a.Name == "DisplayName"),
                ConnectedSystemAttributeId = hrType.Attributes.Single(a => a.Name == "DisplayName").Id } }
        });

        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "user", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "displayName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = ExchangeRecipientAttributes.RecipientType, Type = AttributeDataType.Text, Selected = true, IsConnectorDefined = true }
        });
        var exportRule = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Export", enableProvisioning: true);
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule, SyncRuleId = exportRule.Id,
            TargetConnectedSystemAttribute = directoryType.Attributes.Single(a => a.Name == "displayName"),
            TargetConnectedSystemAttributeId = directoryType.Attributes.Single(a => a.Name == "displayName").Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = displayName, MetaverseAttributeId = displayName.Id } }
        });
        var recipientType = directoryType.Attributes.Single(a => a.Name == ExchangeRecipientAttributes.RecipientType);
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule, SyncRuleId = exportRule.Id,
            TargetConnectedSystemAttribute = recipientType, TargetConnectedSystemAttributeId = recipientType.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = $"\"{ExchangeRecipientTypes.Mailbox}\"" } }
        });
        SyncRepo.SeedSyncRule(exportRule);

        await CreateCsoAsync(hr.Id, hrType, "Alice Smith", "E1001");
        await DbContext.SaveChangesAsync();
        return (hr, directory);
    }

    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }

    private async Task<Activity> RunExportAsync(ConnectedSystem connectedSystem)
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, "Export", ConnectedSystemRunType.Export);
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.Export);
        var workerTask = new SynchronisationWorkerTask(reloaded.Id, profile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };
        var connector = new MockCallConnector().WithConnectedSystemExportResultFactory(_ =>
            new ConnectedSystemExportResult { Success = true, DirectoryServer = DomainController });
        await new SyncExportTaskProcessor(new SyncServer(Jim), SyncRepo, connector, reloaded, profile, workerTask, new CancellationTokenSource())
            .PerformExportAsync();
        return activity;
    }
}
