// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.CompilerServices;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// What a Full Import keeps of each Connector page for deletion detection (#2521, Phase 3): the External ID values, by
/// Object Type, and nothing of the objects that carried them. It used to keep each object's External ID attribute as the
/// Connector returned it, with its typed value lists, which at 500,000 objects was about 212 MB held to the end of the run.
/// </summary>
[TestFixture]
public class FullImportImportedExternalIdsTests : WorkflowTestBase
{
    private const int ObjectsPerPage = 20;

    [Test]
    public async Task FullImport_LaterPage_HoldsNoExternalIdAttributeOfAnEarlierPageAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        var user = await CreateCsoTypeAsync(connectedSystem.Id, "user");
        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);
        var workerTask = new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        var firstPageExternalIdAttributes = new List<WeakReference>();
        int? firstPageAttributesStillHeld = null;
        var connector = new GeneratedPagesConnector(pageCount: 3,
            page: index => Page(user.Name, index == 0 ? firstPageExternalIdAttributes : null),
            beforePage: index =>
            {
                // The import holds the page before this one as it asks for the next, so the first page is everything the
                // import has finished with by the third call.
                if (index != 2)
                    return;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                firstPageAttributesStillHeld = firstPageExternalIdAttributes.Count(reference => reference.IsAlive);
            });

        var processor = new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(), connector, connectedSystem,
            runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstPageExternalIdAttributes, Has.Count.EqualTo(ObjectsPerPage), "precondition: the first page was read");
            Assert.That(firstPageAttributesStillHeld, Is.Zero, "the first page's External ID attributes are released once it is processed");
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(3 * ObjectsPerPage),
                "every page's objects are imported");
        }
    }

    /// <summary>A page of users, noting a weak reference to each one's External ID attribute when asked to.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<ConnectedSystemImportObject> Page(string objectType, List<WeakReference>? externalIdAttributes)
    {
        var objects = new List<ConnectedSystemImportObject>(ObjectsPerPage);
        for (var i = 0; i < ObjectsPerPage; i++)
        {
            var externalId = new ConnectedSystemImportObjectAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, GuidValues = [Guid.NewGuid()] };
            externalIdAttributes?.Add(new WeakReference(externalId));
            objects.Add(new ConnectedSystemImportObject
            {
                ObjectType = objectType,
                Attributes =
                [
                    externalId,
                    new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [$"User {i}"] }
                ]
            });
        }
        return objects;
    }

    /// <summary>
    /// Builds each page when it is asked for and keeps nothing of it once handed over, as a Connector reading a directory
    /// does, so whatever still holds a page afterwards is the import.
    /// </summary>
    private sealed class GeneratedPagesConnector(int pageCount, Func<int, List<ConnectedSystemImportObject>> page, Action<int> beforePage)
        : IConnector, IConnectorImportUsingCalls
    {
        private int _nextPage;

        public string Name => "MockConnector";
        public string? Description => null;
        public string? Url => null;

        public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData, ILogger logger) { }

        public string? CloseImportConnection() => null;

        public Task<ConnectedSystemImportResult> ImportAsync(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile,
            List<ConnectedSystemPaginationToken> paginationTokens, string? persistedConnectorData, ILogger logger, CancellationToken cancellationToken,
            IConnectorProgress progress)
        {
            var pageIndex = _nextPage++;
            beforePage(pageIndex);
            var result = new ConnectedSystemImportResult { ImportObjects = page(pageIndex) };
            if (_nextPage < pageCount)
                result.PaginationTokens.Add(new ConnectedSystemPaginationToken("page", _nextPage.ToString()));
            return Task.FromResult(result);
        }
    }
}
