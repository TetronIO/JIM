// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// An import recognises an object by its External ID, and whether two values name the same object follows the anchor
/// attribute's case-exact declaration (#2051): <c>JBloggs</c> and <c>jbloggs</c> are one object unless the Connected
/// System declares the attribute case-exact, when they are two.
/// </summary>
[TestFixture]
public class ImportCaseExactExternalIdTests : WorkflowTestBase
{
    [Test]
    public async Task FullImport_AnchorIgnoringCase_MatchesTheHeldObjectInAnotherCaseAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync(caseExact: false);
        var held = SeedCso(connectedSystem, type, "JBloggs", "Original");

        await RunImportAsync(connectedSystem, [[ImportObject(type, "jbloggs", "Updated")]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(1), "The import is the held object");
            Assert.That(DisplayNames(held), Is.EqualTo(new[] { "Updated" }));
        }
    }

    [Test]
    public async Task FullImport_CaseExactAnchor_TreatsAValueInAnotherCaseAsAnotherObjectAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync(caseExact: true);
        var held = SeedCso(connectedSystem, type, "JBloggs", "Original");

        await RunImportAsync(connectedSystem, [[ImportObject(type, "jbloggs", "Someone else")]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DisplayNames(held), Is.EqualTo(new[] { "Original" }), "The held object is not the import");
            Assert.That(SyncRepo.ConnectedSystemObjects.Values.Any(cso => cso.Id != held.Id && AnchorOf(cso) == "jbloggs"), Is.True,
                "The import is created as an object of its own");
        }
    }

    [Test]
    public async Task FullImport_CaseExactAnchor_HeldValueInItsOwnCase_StillMatchesAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync(caseExact: true);
        var held = SeedCso(connectedSystem, type, "JBloggs", "Original");

        await RunImportAsync(connectedSystem, [[ImportObject(type, "JBloggs", "Updated")]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(1));
            Assert.That(DisplayNames(held), Is.EqualTo(new[] { "Updated" }));
        }
    }

    [Test]
    public async Task FullImport_AnchorIgnoringCase_ValuesDifferingOnlyInCaseOnOnePage_AreDuplicatesAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync(caseExact: false);

        var activity = await RunImportAsync(connectedSystem, [[ImportObject(type, "JBloggs"), ImportObject(type, "jbloggs")]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DuplicateItems(activity), Has.Count.EqualTo(2));
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.Zero);
        }
    }

    [Test]
    public async Task FullImport_CaseExactAnchor_ValuesDifferingOnlyInCaseOnOnePage_AreTwoObjectsAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync(caseExact: true);

        var activity = await RunImportAsync(connectedSystem, [[ImportObject(type, "JBloggs"), ImportObject(type, "jbloggs")]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DuplicateItems(activity), Is.Empty);
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(2));
        }
    }

    [Test]
    public async Task FullImport_CaseExactAnchor_ValuesDifferingOnlyInCaseOnTwoPages_AreTwoObjectsAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync(caseExact: true);

        var activity = await RunImportAsync(connectedSystem, [[ImportObject(type, "JBloggs")], [ImportObject(type, "jbloggs")]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DuplicateItems(activity), Is.Empty);
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(2));
        }
    }

    [Test]
    public async Task FullImport_CaseExactAnchor_ObjectMissingFromTheImport_IsDeletedNotItsNamesakeInAnotherCaseAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync(caseExact: true);
        var returned = SeedCso(connectedSystem, type, "JBloggs", "Returned");
        var missing = SeedCso(connectedSystem, type, "jbloggs", "Missing");

        await RunImportAsync(connectedSystem, [[ImportObject(type, "JBloggs", "Returned")]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(returned.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), "The object the import returned is kept");
            Assert.That(StatusOf(missing.Id), Is.Not.EqualTo(ConnectedSystemObjectStatus.Normal),
                "The object the import did not return is the one handled as deleted");
        }
    }

    [Test]
    public async Task FullImport_AnchorIgnoringCase_HeldObjectReturnedInAnotherCase_IsNotDeletedAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync(caseExact: false);
        var held = SeedCso(connectedSystem, type, "JBloggs", "Original");

        await RunImportAsync(connectedSystem, [[ImportObject(type, "jbloggs", "Original")]]);

        Assert.That(StatusOf(held.Id), Is.EqualTo(ConnectedSystemObjectStatus.Normal));
    }

    #region Helpers

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType Type)> ArrangeAsync(bool caseExact)
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        var type = await CreateCsoTypeAsync(connectedSystem.Id, "user",
        [
            new() { Name = "AccountId", Type = AttributeDataType.Text, IsExternalId = true, CaseExact = caseExact, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
        ]);
        return (connectedSystem, type);
    }

    private async Task<Activity> RunImportAsync(ConnectedSystem connectedSystem, List<List<ConnectedSystemImportObject>> pages)
    {
        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);
        var workerTask = new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new PagedImportConnector(pages), connectedSystem, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformImportAsync();
        return activity;
    }

    private static List<ActivityRunProfileExecutionItem> DuplicateItems(Activity activity) =>
        activity.RunProfileExecutionItems
            .Where(item => item.ErrorType == ActivityRunProfileExecutionItemErrorType.DuplicateObject)
            .ToList();

    private ConnectedSystemObject SeedCso(ConnectedSystem connectedSystem, ConnectedSystemObjectType type, string accountId, string displayName)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            TypeId = type.Id,
            Type = type,
            ExternalIdAttributeId = type.Attributes.Single(a => a.IsExternalId).Id,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        };
        foreach (var (attribute, value) in new[]
                 {
                     (type.Attributes.Single(a => a.IsExternalId), accountId),
                     (type.Attributes.Single(a => a.Name == "DisplayName"), displayName)
                 })
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = attribute.Id,
                Attribute = attribute,
                StringValue = value,
                ConnectedSystemObject = cso
            });
        }

        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private ConnectedSystemObjectStatus? StatusOf(Guid csoId) =>
        SyncRepo.ConnectedSystemObjects.TryGetValue(csoId, out var cso) ? cso.Status : null;

    private static string? AnchorOf(ConnectedSystemObject cso) =>
        cso.AttributeValues.FirstOrDefault(v => v.AttributeId == cso.ExternalIdAttributeId)?.StringValue;

    private static string?[] DisplayNames(ConnectedSystemObject cso) =>
        cso.AttributeValues.Where(v => v.Attribute?.Name == "DisplayName").Select(v => v.StringValue).ToArray();

    private static ConnectedSystemImportObject ImportObject(ConnectedSystemObjectType type, string accountId, string? displayName = null)
    {
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = type.Name,
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute
                {
                    Name = type.Attributes.Single(a => a.IsExternalId).Name,
                    Type = AttributeDataType.Text,
                    StringValues = [accountId]
                }
            ]
        };
        if (displayName != null)
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [displayName] });
        return importObject;
    }

    #endregion
}
