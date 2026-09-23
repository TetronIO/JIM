// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.InMemoryData;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Models;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// What deselecting a Connected System Object Type does to the objects already imported from it (#1474).
///
/// Deselecting an Object Type takes it out of management, exactly as deselecting a Partition or a Container does:
/// its objects stop being imported, so the next Full Import finds them missing and deletion detection obsoletes
/// them, and the following synchronisation disconnects them and recalls what they contributed. It goes through the
/// same deletion detection as any other missing object, so the Run Profile's deletion limits hold it back too.
///
/// The one exception is the configuration JIM refuses to save: a deselected Object Type an enabled Synchronisation
/// Rule is still bound to. A database can still hold one (saved before the refusal existed, or changed outside the
/// save paths), and obsoleting its objects while an outbound rule still targets the type would disconnect them only
/// for the rule to provision them again. Those objects are left as they are and the Activity says why, rather than
/// cascading.
/// </summary>
[TestFixture]
public class DeselectedObjectTypeDeletionDetectionTests
{
    private const int ConnectedSystemId = 1;
    private const int SelectedTypeId = 100;
    private const int DeselectedTypeId = 200;
    private const int NeverConfiguredTypeId = 300;
    private const int SelectedTypeExternalIdAttributeId = 10;
    private const int DeselectedTypeExternalIdAttributeId = 20;

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_ObjectTypeIsDeselected_ItsObjectsAreObsoletedAsync()
    {
        var fixture = BuildFixture();

        var queuedForUpdate = await DetectDeletionsAsync(fixture.Processor);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedForUpdate.Select(cso => cso.Id), Does.Contain(fixture.SelectedCsoId),
                "the selected type's object was absent from the import, so deletion detection must obsolete it. " +
                "This is the control: it failing means the fixture never reached deletion detection.");

            Assert.That(queuedForUpdate.Select(cso => cso.Id), Does.Contain(fixture.DeselectedCsoId),
                "a deselected Object Type is out of management, so its objects are no longer imported and must be " +
                "obsoleted like any other object the Full Import did not return, as deselecting a Partition does.");

            Assert.That(queuedForUpdate.Single(cso => cso.Id == fixture.DeselectedCsoId).Status,
                Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));

            Assert.That(fixture.Activity.WarningMessage, Is.Null.Or.Empty,
                "nothing was held back, so there is nothing to warn about.");
        }
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_DeselectedObjectTypeHasEnabledSyncRule_ItsObjectsAreLeftInPlaceAndTheActivityWarnsAsync()
    {
        var fixture = BuildFixture();
        fixture.Repository.SeedSyncRule(BuildSyncRule(1, "Provision groups to Glitterband", DeselectedTypeId, enabled: true));

        var queuedForUpdate = await DetectDeletionsAsync(fixture.Processor);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedForUpdate.Select(cso => cso.Id), Does.Contain(fixture.SelectedCsoId),
                "only the deselected type is held back; deletion detection still runs for everything else.");

            Assert.That(queuedForUpdate.Select(cso => cso.Id), Does.Not.Contain(fixture.DeselectedCsoId),
                "an enabled Synchronisation Rule still manages the deselected type, so obsoleting its objects would " +
                "disconnect them only for the rule to act on them again. They must be left as they are.");

            Assert.That(fixture.Activity.WarningMessage, Does.Contain("Group"),
                "the Activity must name the Object Type that was held back, or the administrator cannot find it.");

            Assert.That(fixture.Activity.WarningMessage, Does.Contain("Provision groups to Glitterband"),
                "the Activity must name the Synchronisation Rule to disable, because that is the fix.");
        }
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_DeselectedObjectTypeHasOnlyDisabledSyncRule_ItsObjectsAreObsoletedAsync()
    {
        var fixture = BuildFixture();
        fixture.Repository.SeedSyncRule(BuildSyncRule(1, "Old group rule", DeselectedTypeId, enabled: false));

        var queuedForUpdate = await DetectDeletionsAsync(fixture.Processor);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedForUpdate.Select(cso => cso.Id), Does.Contain(fixture.DeselectedCsoId),
                "a disabled Synchronisation Rule manages nothing, so it must not hold the deselected type's objects back.");

            Assert.That(fixture.Activity.WarningMessage, Is.Null.Or.Empty);
        }
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_DeselectedObjectTypeWithNoExternalId_IsSkippedAsync()
    {
        // A directory's schema discovery can list hundreds of Object Types an administrator never selects; one that was
        // never configured has no External ID and so cannot hold any objects. It must be passed over, not fail the run.
        var fixture = BuildFixture(includeNeverConfiguredType: true);

        var queuedForUpdate = await DetectDeletionsAsync(fixture.Processor);

        Assert.That(queuedForUpdate.Select(cso => cso.Id), Is.EquivalentTo(new[] { fixture.SelectedCsoId, fixture.DeselectedCsoId }));
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_DeselectedObjectTypeExceedsDeletionLimit_NothingIsObsoletedAsync()
    {
        // Deselecting a type can take a great many objects out of management at once, which is exactly what the Run
        // Profile's deletion limits exist to catch. The deselected type's objects are counted like any other.
        var fixture = BuildFixture(maxDetectedDeletions: 1);

        var queuedForUpdate = await DetectDeletionsAsync(fixture.Processor, existingCsoCount: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedForUpdate, Is.Empty,
                "two objects would be newly obsoleted against a limit of one, so the whole detection is withheld.");
            Assert.That(fixture.Activity.DetectedDeletionsWithheld, Is.EqualTo(2),
                "the deselected type's object is counted towards the limit alongside the selected type's.");
        }
    }

    /// <summary>
    /// Invokes deletion detection exactly as a Full Import does at the end of its run, with an empty set of
    /// imported external ids: nothing came back, so every object in scope is a candidate for obsoletion.
    /// </summary>
    private static async Task<List<ConnectedSystemObject>> DetectDeletionsAsync(SyncImportTaskProcessor processor, int existingCsoCount = 0)
    {
        const string methodName = "ProcessConnectedSystemObjectDeletionsAsync";
        var method = typeof(SyncImportTaskProcessor).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{methodName} was not found. If it has been renamed or its signature has " +
                "changed, update this fixture to invoke the current production method; do not reimplement its " +
                "object type filter here, because that filter is the behaviour under test.");

        var queuedForUpdate = new List<ConnectedSystemObject>();
        await (Task)method.Invoke(processor, [new List<ExternalIdPair>(), queuedForUpdate, null, existingCsoCount])!;
        return queuedForUpdate;
    }

    private sealed record Fixture(
        SyncImportTaskProcessor Processor,
        SyncRepository Repository,
        Activity Activity,
        Guid SelectedCsoId,
        Guid DeselectedCsoId);

    private static Fixture BuildFixture(bool includeNeverConfiguredType = false, int? maxDetectedDeletions = null)
    {
        var repository = new SyncRepository();

        var selectedCsoId = SeedObject(repository, SelectedTypeId, SelectedTypeExternalIdAttributeId, "keeper");
        var deselectedCsoId = SeedObject(repository, DeselectedTypeId, DeselectedTypeExternalIdAttributeId, "leaver");

        var objectTypes = new List<ConnectedSystemObjectType>
        {
            BuildObjectType(SelectedTypeId, "User", SelectedTypeExternalIdAttributeId, selected: true),
            BuildObjectType(DeselectedTypeId, "Group", DeselectedTypeExternalIdAttributeId, selected: false)
        };

        if (includeNeverConfiguredType)
        {
            objectTypes.Add(new ConnectedSystemObjectType
            {
                Id = NeverConfiguredTypeId,
                Name = "printQueue",
                ConnectedSystemId = ConnectedSystemId,
                Selected = false,
                Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 30, Name = "cn", Type = AttributeDataType.Text }]
            });
        }

        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Glitterband",
            ObjectTypes = objectTypes
        };

        var runProfile = new ConnectedSystemRunProfile
        {
            Name = "Full Import",
            RunType = ConnectedSystemRunType.FullImport,
            ConnectedSystemId = ConnectedSystemId,
            MaxDetectedDeletions = maxDetectedDeletions
        };

        var activity = new Activity();
        var workerTask = TestUtilities.CreateTestWorkerTask(activity, initiatedBy: null);
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

        return new Fixture(processor, repository, activity, selectedCsoId, deselectedCsoId);
    }

    private static SyncRule BuildSyncRule(int id, string name, int objectTypeId, bool enabled) =>
        new()
        {
            Id = id,
            Name = name,
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectTypeId = objectTypeId,
            Direction = SyncRuleDirection.Export,
            Enabled = enabled
        };

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
