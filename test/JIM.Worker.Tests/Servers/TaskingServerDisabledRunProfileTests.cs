// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A disabled Run Profile cannot be queued (#2740). <c>TaskingServer.CreateWorkerTaskAsync</c> is the one place every run
/// is queued through, whether it was started in the portal, through the REST API, with PowerShell or by a Schedule, so
/// refusing there refuses it everywhere, with one reason.
/// </summary>
[TestFixture]
public class TaskingServerDisabledRunProfileTests
{
    private const int ConnectedSystemId = 1;
    private const int RunProfileId = 10;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private JimApplication _application = null!;
    private ConnectedSystemRunProfile _runProfile = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _mockTaskingRepository = new Mock<ITaskingRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepository.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);

        _runProfile = new ConnectedSystemRunProfile
        {
            Id = RunProfileId,
            ConnectedSystemId = ConnectedSystemId,
            Name = "Full Import (Glitterband)",
            RunType = ConnectedSystemRunType.FullImport
        };

        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Panoply Directory",
            ConnectorDefinition = new ConnectorDefinition { Name = "Test Connector", SupportsPartitions = false },
            RunProfiles = [_runProfile]
        };

        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemRunProfilesAsync(ConnectedSystemId)).ReturnsAsync(() => [_runProfile]);

        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task CreateWorkerTaskAsync_DisabledRunProfile_RefusesWithTheReasonAndQueuesNothingAsync()
    {
        _runProfile.Enabled = false;
        _runProfile.DisabledReason = ConnectedSystemRunProfileDisabledReason.PartitionRemoved;
        _runProfile.DisabledPartitionName = "dc=glitterband,dc=local";

        var result = await _application.Tasking.CreateWorkerTaskAsync(SynchronisationWorkerTask.ForUser(ConnectedSystemId, RunProfileId, Guid.NewGuid(), "Ada Lovelace"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False, "a disabled Run Profile must not be queued");
            Assert.That(result.ErrorMessage, Is.EqualTo(RunProfileEnablement.DescribeRunRefusal(_runProfile)),
                "the refusal names the Run Profile, why it is disabled and what to do");
            Assert.That(result.ErrorMessage, Does.Contain("dc=glitterband,dc=local"));
        }

        _mockTaskingRepository.Verify(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Never, "nothing is queued");
        _mockActivityRepository.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never,
            "and no run Activity is started; a Schedule records its own Failed Activity for a step it could not queue");
    }

    [Test]
    public async Task CreateWorkerTaskAsync_EnabledRunProfile_QueuesItAsync()
    {
        var result = await _application.Tasking.CreateWorkerTaskAsync(SynchronisationWorkerTask.ForUser(ConnectedSystemId, RunProfileId, Guid.NewGuid(), "Ada Lovelace"));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        _mockTaskingRepository.Verify(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Once);
    }
}
