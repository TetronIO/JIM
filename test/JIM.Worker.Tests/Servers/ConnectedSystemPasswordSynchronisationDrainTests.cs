// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Moq;
using NUnit.Framework;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Draining what accumulated while a Connected System was not taking passwords (#1119, requirement 3).
/// <para>
/// Parking is otherwise a one-way door. A change parks because the same configuration produces the same refusal,
/// so the administrator correcting that configuration is the only event that makes another attempt worth making;
/// saving is where it has to reach the parked work. The portal already promises this in as many words when
/// Password Synchronisation is switched on, so it lives on the server where the REST API and PowerShell reach it
/// too, not in the page that says it.
/// </para>
/// </summary>
[TestFixture]
public class ConnectedSystemPasswordSynchronisationDrainTests
{
    private const int ConnectedSystemId = 6;
    private const int UserObjectTypeId = 200;

    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private SyncRepository _syncRepository = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _initiatedBy = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        var activityRepository = new Mock<IActivityRepository>();
        activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        activityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        // No tasking repository: releasing parked work no longer raises a Worker Task (#1635). The row update
        // itself is what wakes the Password Delivery Service, so what these fixtures observe is the row.
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);
        repository.Setup(r => r.Activity).Returns(activityRepository.Object);

        _syncRepository = new SyncRepository();
        _jim = new JimApplication(repository.Object, syncRepository: _syncRepository);
        _initiatedBy = TestUtilities.GetInitiatedBy();
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    /// <summary>
    /// Tells the repository what the stored configuration looked like before the save.
    /// </summary>
    private void ArrangeStoredConfiguration(ConnectedSystemPasswordSynchronisation? stored) =>
        _connectedSystemRepository
            .Setup(r => r.GetPasswordSynchronisationAsync(ConnectedSystemId))
            .ReturnsAsync(stored);

    private static ConnectedSystemPasswordSynchronisation Configuration(bool enabled, int maxRetries = 3) => new()
    {
        ConnectedSystemId = ConnectedSystemId,
        Enabled = enabled,
        TargetObjectTypeId = UserObjectTypeId,
        MaxRetries = maxRetries,
        RetryBackoffBase = TimeSpan.FromMinutes(5)
    };

    /// <summary>
    /// A Connected System complete enough for the update path to accept it: it validates the Connector Definition
    /// and settings before it reaches anything to do with passwords.
    /// </summary>
    private static ConnectedSystem System(ConnectedSystemPasswordSynchronisation? configuration) => new()
    {
        Id = ConnectedSystemId,
        Name = "Corporate AD",
        ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "JIM LDAP Connector" },
        SettingValues =
        [
            new ConnectedSystemSettingValue
            {
                Id = 1,
                Setting = new ConnectorDefinitionSetting { Id = 1, Name = "Server", Required = false },
                StringValue = "dc.example.test"
            }
        ],
        PasswordSynchronisation = configuration
    };

    /// <summary>
    /// Puts a parked change on the queue and returns it, so a test can see whether saving released it.
    /// </summary>
    private async Task<PendingPasswordChange> ArrangeParkedChangeAsync()
    {
        var now = DateTime.UtcNow;
        var change = new PendingPasswordChange
        {
            MetaverseObjectId = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            EncryptedPassword = "$JIMPW$v1$ciphertext",
            Status = PendingPasswordChangeStatus.Parked,
            FailureReason = PasswordSetFailureReason.PolicyRejection,
            AttemptCount = 3,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7),
            ActivityId = Guid.NewGuid()
        };

        await _syncRepository.QueuePasswordChangesAsync([change]);
        return _syncRepository.PendingPasswordChanges[change.Id];
    }

    [Test]
    public async Task UpdateConnectedSystem_PasswordSynchronisationTurnedOn_ReleasesParkedChangesAsync()
    {
        var parked = await ArrangeParkedChangeAsync();
        ArrangeStoredConfiguration(Configuration(enabled: false));

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(System(Configuration(enabled: true)), _initiatedBy);

        Assert.That(parked.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending),
            "Work that accumulated while the system was off must be deliverable once it is on.");
    }

    [Test]
    public async Task UpdateConnectedSystem_DeliverySettingChanged_ReleasesParkedChangesAsync()
    {
        // A parked change is one the target refused. Changing what JIM will send is what makes another attempt
        // worth making.
        var parked = await ArrangeParkedChangeAsync();
        ArrangeStoredConfiguration(Configuration(enabled: true, maxRetries: 3));

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(System(Configuration(enabled: true, maxRetries: 8)), _initiatedBy);

        Assert.That(parked.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
    }

    [Test]
    public async Task UpdateConnectedSystem_NothingAboutDeliveryChanged_LeavesParkedChangesAloneAsync()
    {
        // An unrelated edit to the Connected System. Releasing here would retry against settings the target has
        // already answered on, failing identically and inflating an attempt count that counts distinct
        // configurations tried.
        var parked = await ArrangeParkedChangeAsync();
        ArrangeStoredConfiguration(Configuration(enabled: true));

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(System(Configuration(enabled: true)), _initiatedBy);

        Assert.That(parked.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
    }

    [Test]
    public async Task UpdateConnectedSystem_PasswordSynchronisationTurnedOff_LeavesParkedChangesAloneAsync()
    {
        // Requirement 2: a disabled system accumulates rather than discarding, and releasing work onto a system
        // that will not deliver it would only churn.
        var parked = await ArrangeParkedChangeAsync();
        ArrangeStoredConfiguration(Configuration(enabled: true));

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(System(Configuration(enabled: false)), _initiatedBy);

        Assert.That(parked.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
    }

    [Test]
    public async Task UpdateConnectedSystem_NeverConfiguredForPasswordSynchronisation_LeavesParkedChangesAloneAsync()
    {
        var parked = await ArrangeParkedChangeAsync();
        ArrangeStoredConfiguration(null);

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(System(null), _initiatedBy);

        Assert.That(parked.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
    }

    [Test]
    public async Task UpdateConnectedSystemByApiKey_PasswordSynchronisationTurnedOn_ReleasesParkedChangesAsync()
    {
        // Surface parity: an administrator scripting the same change gets the same drain.
        var parked = await ArrangeParkedChangeAsync();
        ArrangeStoredConfiguration(Configuration(enabled: false));

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(
            System(Configuration(enabled: true)),
            new JIM.Models.Security.ApiKey { Id = Guid.NewGuid(), Name = "Automation" });

        Assert.That(parked.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
    }
}
