// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Connectors;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// An import through a Connector behind a feature flag (the SCIM 2.0 Service Provider, D16 in SCIM_SERVER_DESIGN.md):
/// a Connected System created while the flag was on, and imported after it was turned off, fails saying why rather
/// than importing nothing.
/// </summary>
[TestFixture]
public class FeatureFlaggedConnectorImportTests : WorkflowTestBase
{
    [Test]
    public async Task PerformImportAsync_FlagOff_FailsNamingTheFeatureAsync()
    {
        var (connectedSystem, runProfile, workerTask) = await ArrangeAsync();

        var processor = new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new PagedImportConnector([[]]), connectedSystem, runProfile, workerTask, new CancellationTokenSource());

        Assert.That(processor.PerformImportAsync, Throws.InstanceOf<CannotPerformImportException>()
            .With.Message.Contains(FeatureFlagCatalogue.ScimServiceProvider.DisplayName));
    }

    [Test]
    public async Task PerformImportAsync_FlagOn_ImportsAsync()
    {
        DbContext.ServiceSettingItems.Add(new ServiceSetting
        {
            Key = FeatureFlagCatalogue.ScimServiceProvider.Key,
            DisplayName = FeatureFlagCatalogue.ScimServiceProvider.DisplayName,
            Category = ServiceSettingCategory.FeatureFlags,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "false",
            Value = "true"
        });
        await DbContext.SaveChangesAsync();
        var (connectedSystem, runProfile, workerTask) = await ArrangeAsync();

        var processor = new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new PagedImportConnector([[]]), connectedSystem, runProfile, workerTask, new CancellationTokenSource());

        Assert.That(processor.PerformImportAsync, Throws.Nothing);
    }

    private async Task<(ConnectedSystem, ConnectedSystemRunProfile, SynchronisationWorkerTask)> ArrangeAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Entra provisioning");
        connectedSystem.ConnectorDefinition.Name = ConnectorConstants.ScimServiceProviderConnectorName;
        await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);
        var workerTask = new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };
        return (connectedSystem, runProfile, workerTask);
    }
}
