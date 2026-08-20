// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.InMemoryData;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Worker.Models;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// What deselecting a Connected System Object Type actually does to the objects already imported from it.
///
/// Deletion detection walks <c>ObjectTypes.Where(ot =&gt; ot.Selected)</c>, so a deselected type is not
/// considered at all: its Connected System Objects are never compared against the import and never obsoleted.
/// They stay <see cref="ConnectedSystemObjectStatus.Normal"/>, stay joined to whatever they are joined to, and
/// keep contributing their last-imported values to the Metaverse for as long as the deselection stands.
///
/// This is the opposite of deselecting a container or a partition, where the objects fall out of an import that
/// still runs and are obsoleted as missing. The asymmetry is what the administrator-facing consequence copy has
/// to state honestly, and it is pinned here because the copy is written against it.
/// </summary>
[TestFixture]
public class DeselectedObjectTypeDeletionDetectionTests
{
    private const int ConnectedSystemId = 1;
    private const int SelectedTypeId = 100;
    private const int DeselectedTypeId = 200;
    private const int SelectedTypeExternalIdAttributeId = 10;
    private const int DeselectedTypeExternalIdAttributeId = 20;

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_ObjectTypeIsDeselected_ItsObjectsAreNotObsoletedAsync()
    {
        var (processor, selectedCsoId, deselectedCsoId) = BuildFixture();

        var queuedForUpdate = await DetectDeletionsAsync(processor);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedForUpdate.Select(cso => cso.Id), Does.Contain(selectedCsoId),
                "the selected type's object was absent from the import, so deletion detection must obsolete it. " +
                "This is the control: it failing means the fixture never reached deletion detection.");

            Assert.That(queuedForUpdate.Single(cso => cso.Id == selectedCsoId).Status,
                Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));

            Assert.That(queuedForUpdate.Select(cso => cso.Id), Does.Not.Contain(deselectedCsoId),
                "a deselected Object Type is skipped by deletion detection entirely, so its objects are left " +
                "exactly as they were. Deselecting a type does not obsolete or deprovision anything; it freezes " +
                "the objects in place. Any consequence copy that promises obsoletion is wrong.");
        }
    }

    /// <summary>
    /// Invokes deletion detection exactly as a Full Import does at the end of its run, with an empty set of
    /// imported external ids: nothing came back, so every object in a selected type is a candidate for obsoletion.
    /// </summary>
    private static async Task<List<ConnectedSystemObject>> DetectDeletionsAsync(SyncImportTaskProcessor processor)
    {
        const string methodName = "ProcessConnectedSystemObjectDeletionsAsync";
        var method = typeof(SyncImportTaskProcessor).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{methodName} was not found. If it has been renamed or its signature has " +
                "changed, update this fixture to invoke the current production method; do not reimplement its " +
                "object type selection filter here, because that filter is the behaviour under test.");

        var queuedForUpdate = new List<ConnectedSystemObject>();
        await (Task)method.Invoke(processor, [new List<ExternalIdPair>(), queuedForUpdate, null])!;
        return queuedForUpdate;
    }

    private static (SyncImportTaskProcessor Processor, Guid SelectedCsoId, Guid DeselectedCsoId) BuildFixture()
    {
        var repository = new SyncRepository();

        var selectedCsoId = SeedObject(repository, SelectedTypeId, SelectedTypeExternalIdAttributeId, "keeper");
        var deselectedCsoId = SeedObject(repository, DeselectedTypeId, DeselectedTypeExternalIdAttributeId, "frozen");

        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Glitterband",
            ObjectTypes =
            [
                BuildObjectType(SelectedTypeId, "User", SelectedTypeExternalIdAttributeId, selected: true),
                BuildObjectType(DeselectedTypeId, "Group", DeselectedTypeExternalIdAttributeId, selected: false)
            ]
        };

        var runProfile = new ConnectedSystemRunProfile
        {
            Name = "Full Import",
            RunType = ConnectedSystemRunType.FullImport,
            ConnectedSystemId = ConnectedSystemId
        };

        var workerTask = TestUtilities.CreateTestWorkerTask(new Activity(), initiatedBy: null);
        var cancellationTokenSource = new CancellationTokenSource();

        var processor = new SyncImportTaskProcessor(
            null!,
            repository,
            null!,
            new SyncEngine(),
            new MockFileConnector(),
            connectedSystem,
            runProfile,
            workerTask,
            cancellationTokenSource);

        return (processor, selectedCsoId, deselectedCsoId);
    }

    private static ConnectedSystemObjectType BuildObjectType(int id, string name, int externalIdAttributeId, bool selected) =>
        new()
        {
            Id = id,
            Name = name,
            ConnectedSystemId = ConnectedSystemId,
            Selected = selected,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute
                {
                    Id = externalIdAttributeId,
                    Name = "id",
                    Type = AttributeDataType.Text,
                    IsExternalId = true,
                    Selected = true
                }
            ]
        };

    private static Guid SeedObject(SyncRepository repository, int objectTypeId, int externalIdAttributeId, string externalId)
    {
        var id = Guid.NewGuid();
        repository.SeedConnectedSystemObject(new ConnectedSystemObject
        {
            Id = id,
            ConnectedSystemId = ConnectedSystemId,
            TypeId = objectTypeId,
            ExternalIdAttributeId = externalIdAttributeId,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow,
            AttributeValues =
            [
                new ConnectedSystemObjectAttributeValue
                {
                    Id = Guid.NewGuid(),
                    AttributeId = externalIdAttributeId,
                    StringValue = externalId
                }
            ]
        });
        return id;
    }
}
