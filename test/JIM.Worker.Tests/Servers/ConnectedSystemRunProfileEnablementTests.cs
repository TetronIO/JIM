// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Enabling and disabling a Run Profile through an update (#2740), on every surface: the portal, the REST API and
/// PowerShell all save through <c>ConnectedSystemServer.UpdateConnectedSystemRunProfileAsync</c>. The server judges the
/// update against what is stored, records why a Run Profile is disabled, and refuses to enable one a removed Partition
/// disabled unless the update says what it reads.
/// </summary>
[TestFixture]
public class ConnectedSystemRunProfileEnablementTests
{
    private const int ConnectedSystemId = 1;
    private const int RunProfileId = 10;

    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private JimApplication _jim = null!;
    private ConnectedSystemRunProfileEnabledState? _stored;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        repository.Setup(r => r.Activity).Returns(new Mock<IActivityRepository>().Object);
        _connectedSystems.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(new ConnectedSystem { Id = ConnectedSystemId, Name = "Panoply Directory" });
        _connectedSystems.Setup(r => r.GetConnectedSystemRunProfileEnabledStateAsync(RunProfileId)).ReturnsAsync(() => _stored);
        _jim = new JimApplication(repository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _jim.Dispose();
    }

    private static MetaverseObject Administrator() => new() { Id = Guid.NewGuid(), CachedDisplayName = "Ada Lovelace" };

    private static ConnectedSystemRunProfile RunProfile(bool enabled, ConnectedSystemPartition? partition = null) => new()
    {
        Id = RunProfileId,
        ConnectedSystemId = ConnectedSystemId,
        Name = "Full Import (Glitterband)",
        RunType = ConnectedSystemRunType.FullImport,
        Enabled = enabled,
        Partition = partition
    };

    [Test]
    public async Task UpdateConnectedSystemRunProfileAsync_Disabling_RecordsThatAnAdministratorDisabledItAsync()
    {
        _stored = new ConnectedSystemRunProfileEnabledState(true, null, null);
        var runProfile = RunProfile(enabled: false);

        await _jim.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(runProfile, Administrator());

        _connectedSystems.Verify(r => r.UpdateConnectedSystemRunProfileAsync(It.Is<ConnectedSystemRunProfile>(rp =>
            !rp.Enabled && rp.DisabledReason == ConnectedSystemRunProfileDisabledReason.Manual)), Times.Once);
    }

    [Test]
    public void UpdateConnectedSystemRunProfileAsync_EnablingOneARemovedPartitionDisabledWithNoScope_IsRefusedAndSavesNothing()
    {
        _stored = new ConnectedSystemRunProfileEnabledState(false, ConnectedSystemRunProfileDisabledReason.PartitionRemoved, "dc=glitterband,dc=local");
        var runProfile = RunProfile(enabled: true);

        var ex = Assert.ThrowsAsync<ArgumentException>(() => _jim.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(runProfile, Administrator()));

        Assert.That(ex!.Message, Does.Contain("choose the partition it reads, or choose all selected partitions"),
            "the refusal names the remedy");
        _connectedSystems.Verify(r => r.UpdateConnectedSystemRunProfileAsync(It.IsAny<ConnectedSystemRunProfile>()), Times.Never,
            "re-enabling must never silently widen what it reads");
    }

    [Test]
    public async Task UpdateConnectedSystemRunProfileAsync_EnablingOneARemovedPartitionDisabledForAllSelectedPartitions_EnablesItAsync()
    {
        _stored = new ConnectedSystemRunProfileEnabledState(false, ConnectedSystemRunProfileDisabledReason.PartitionRemoved, "dc=glitterband,dc=local");
        var runProfile = RunProfile(enabled: true);

        await _jim.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(runProfile, new ApiKey { Id = Guid.NewGuid(), Name = "Automation" },
            allSelectedPartitions: true);

        _connectedSystems.Verify(r => r.UpdateConnectedSystemRunProfileAsync(It.Is<ConnectedSystemRunProfile>(rp =>
            rp.Enabled && rp.Partition == null && rp.DisabledReason == null && rp.DisabledPartitionName == null)), Times.Once);
    }

    [Test]
    public async Task UpdateConnectedSystemRunProfileAsync_EnablingOneARemovedPartitionDisabledWithAPartition_EnablesItAsync()
    {
        _stored = new ConnectedSystemRunProfileEnabledState(false, ConnectedSystemRunProfileDisabledReason.PartitionRemoved, "dc=glitterband,dc=local");
        var partition = new ConnectedSystemPartition { Id = 3, Name = "dc=yellowstone,dc=local", ExternalId = "dc=yellowstone,dc=local", Selected = true };
        var runProfile = RunProfile(enabled: true, partition);

        await _jim.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(runProfile, Administrator());

        _connectedSystems.Verify(r => r.UpdateConnectedSystemRunProfileAsync(It.Is<ConnectedSystemRunProfile>(rp =>
            rp.Enabled && rp.Partition == partition && rp.DisabledReason == null)), Times.Once);
    }
}
