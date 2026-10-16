// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A Connected System Object that its Metaverse Object's deletion had already disconnected is deleted quietly when the
/// confirming import finds it gone: no execution item, because the disconnection was recorded when it happened. With
/// Connected System Object change tracking on, its deletion is still recorded, as every other deletion is, so the
/// object's history stays findable by its id after its row is gone.
/// </summary>
[TestFixture]
public class QuietCsoDeletionChangeRecordWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task FullSync_PreDisconnectedObjectFoundObsolete_RecordsItsDeletionAsync()
    {
        var (connectedSystem, cso) = await SeedPreDisconnectedObsoleteObjectAsync();

        await RunFullSyncAsync(connectedSystem);

        var deletion = SyncRepo.StandaloneCsoChanges.SingleOrDefault(c => c.DeletedConnectedSystemObjectId == cso.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(cso.Id), Is.False, "the object is deleted");
            Assert.That(deletion, Is.Not.Null, "its deletion is recorded");
            Assert.That(deletion!.ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
            Assert.That(deletion!.ConnectedSystemId, Is.EqualTo(connectedSystem.Id));
            Assert.That(deletion!.DeletedObjectDisplayName, Is.EqualTo("Tina Adams"));
            Assert.That(deletion!.DeletedObjectExternalId, Is.Not.Null.And.Not.Empty,
                "the external id is what finds the object's execution items once its row is gone");
            Assert.That(deletion!.ActivityRunProfileExecutionItemId, Is.Null, "a quiet deletion has no execution item");
        }
    }

    [Test]
    public async Task FullSync_PreDisconnectedObjectFoundObsoleteWithChangeTrackingOff_RecordsNothingAsync()
    {
        await SetCsoChangeTrackingAsync(false);
        var (connectedSystem, cso) = await SeedPreDisconnectedObsoleteObjectAsync();

        await RunFullSyncAsync(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(cso.Id), Is.False, "the object is deleted");
            Assert.That(SyncRepo.StandaloneCsoChanges, Is.Empty);
        }
    }

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObject Cso)> SeedPreDisconnectedObsoleteObjectAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Glitterband EMEA");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "user");
        var cso = await CreateCsoAsync(connectedSystem.Id, csoType, "Tina Adams", "S8-399");
        cso.ExternalIdAttributeId = csoType.Attributes.Single(a => a.IsExternalId).Id;

        // As the Metaverse Object's synchronous deletion leaves it, then the confirming import finds it gone.
        cso.MetaverseObject = null;
        cso.MetaverseObjectId = null;
        cso.JoinType = ConnectedSystemObjectJoinType.NotJoined;
        cso.Status = ConnectedSystemObjectStatus.Obsolete;
        await ModifyCsoAsync(cso);

        return (connectedSystem, cso);
    }

    private async Task SetCsoChangeTrackingAsync(bool enabled)
    {
        var setting = DbContext.ServiceSettingItems.FirstOrDefault(s => s.Key == Constants.SettingKeys.ChangeTrackingCsoChangesEnabled);
        if (setting == null)
        {
            setting = new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingCsoChangesEnabled,
                ValueType = ServiceSettingValueType.Boolean,
                DefaultValue = "true"
            };
            DbContext.ServiceSettingItems.Add(setting);
        }
        setting.Value = enabled ? "true" : "false";
        await DbContext.SaveChangesAsync();
    }

    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(connectedSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, connectedSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }
}
