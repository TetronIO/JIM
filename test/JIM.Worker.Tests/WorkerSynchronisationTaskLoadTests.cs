// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using Moq;

namespace JIM.Worker.Tests;

/// <summary>
/// The order in which the Worker takes a synchronisation task (#2615). The ids of the configuration changes awaiting
/// synchronisation must be read BEFORE the Connected System is loaded: that read is what bounds the changes the run may
/// claim to ones committed before anything the run uses was loaded, which closes the window in which a Simple Mode
/// Object Matching Rule change could be claimed yet missing from the Object Types already in hand.
/// </summary>
[TestFixture]
public class WorkerSynchronisationTaskLoadTests
{
    private const int ConnectedSystemId = 7;

    [Test]
    public async Task LoadConnectedSystemForSynchronisationAsync_ReadsTheEligibleConfigurationChangeIdsBeforeLoadingTheConnectedSystemAsync()
    {
        TestUtilities.SetEnvironmentVariables();
        var calls = new List<string>();
        var syncRepository = new Mock<ISyncRepository>();
        syncRepository.Setup(r => r.GetConfigurationChangeSynchronisationIdsAwaitingAsync(ConnectedSystemId))
            .Callback(() => calls.Add("ids"))
            .ReturnsAsync((IReadOnlyList<long>)[3L, 4L]);
        var connectedSystems = new Mock<IConnectedSystemRepository>();
        connectedSystems.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, true))
            .Callback(() => calls.Add("connected system"))
            .ReturnsAsync(new ConnectedSystem { Id = ConnectedSystemId, Name = "HR" });
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);
        using var jim = new JimApplication(repository.Object, syncRepository: syncRepository.Object);

        var (connectedSystem, eligibleIds) = await Worker.LoadConnectedSystemForSynchronisationAsync(jim, syncRepository.Object, ConnectedSystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(new[] { "ids", "connected system" }), "the ids are read before the Connected System is loaded");
            Assert.That(connectedSystem?.Id, Is.EqualTo(ConnectedSystemId));
            Assert.That(eligibleIds, Is.EqualTo(new[] { 3L, 4L }));
        }
    }

    [Test]
    public async Task LoadConnectedSystemForSynchronisationAsync_ConnectedSystemNoLongerExists_ReturnsNullAndTheIdsAsync()
    {
        TestUtilities.SetEnvironmentVariables();
        var syncRepository = new Mock<ISyncRepository>();
        syncRepository.Setup(r => r.GetConfigurationChangeSynchronisationIdsAwaitingAsync(ConnectedSystemId)).ReturnsAsync((IReadOnlyList<long>)[]);
        var connectedSystems = new Mock<IConnectedSystemRepository>();
        connectedSystems.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, true)).ReturnsAsync((ConnectedSystem?)null);
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);
        using var jim = new JimApplication(repository.Object, syncRepository: syncRepository.Object);

        var (connectedSystem, eligibleIds) = await Worker.LoadConnectedSystemForSynchronisationAsync(jim, syncRepository.Object, ConnectedSystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connectedSystem, Is.Null);
            Assert.That(eligibleIds, Is.Empty);
        }
    }
}
