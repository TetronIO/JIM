// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests that deleting a Connected System captures a redacted tombstone snapshot on its delete Activity, across all
/// three delete paths (synchronous small-system, background-job for large systems, and queued-after-sync). Like the
/// Synchronisation Rule delete tombstone, the snapshot is unversioned and the Activity is left unlinked (the object is
/// removed before the Activity completes). An optional change reason is recorded on the Activity, including for the
/// asynchronous worker paths where the reason is carried on the queued Activity.
/// </summary>
[TestFixture]
public class ConnectedSystemDeletionConfigurationChangeCaptureTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private Mock<IMetaverseRepository> _mvRepo = null!;
    private Mock<ITaskingRepository> _taskingRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private readonly List<Activity> _createdActivities = [];
    private Activity? _updatedActivity;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _createdActivities.Clear();
        _updatedActivity = null;

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _mvRepo = new Mock<IMetaverseRepository>();
        _taskingRepo = new Mock<ITaskingRepository>();

        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_mvRepo.Object);
        _repo.Setup(r => r.Tasking).Returns(_taskingRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _createdActivities.Add(a))
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _updatedActivity = a)
            .Returns(Task.CompletedTask);

        _csRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.DeleteConnectedSystemAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetRunningSyncTaskAsync(It.IsAny<int>())).ReturnsAsync((SynchronisationWorkerTask?)null);

        _mvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<MetaverseObject>());
        _mvRepo.Setup(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(0);

        _taskingRepo.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>())).Returns(Task.CompletedTask);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task DeleteAsync_SmallSystem_WhenTrackingEnabled_CapturesTombstoneSnapshotWithReasonAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupCore(BuildCore());
        SetupFull(BuildFull());
        _csRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(10); // small: synchronous

        var result = await _jim.ConnectedSystems.DeleteAsync(1, TestUtilities.GetInitiatedBy(), deleteChangeHistory: false, changeReason: "decommissioned (CHG0123)");

        Assert.That(result.Success, Is.True);
        Assert.That(_updatedActivity, Is.Not.Null, "the delete Activity is completed via an update");
        Assert.That(_updatedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"ConnectedSystem\""),
            "the deletion records a tombstone snapshot of the Connected System");
        Assert.That(_updatedActivity.ChangeReason, Is.EqualTo("decommissioned (CHG0123)"));
        Assert.That(_updatedActivity.ConfigurationChangeVersion, Is.Null, "deletions record a tombstone, not a versioned entry");
        Assert.That(_updatedActivity.ConnectedSystemId, Is.Null, "the delete Activity is left unlinked so it can complete after the system is removed");
    }

    [Test]
    public async Task DeleteAsync_SmallSystem_WhenTrackingDisabled_RecordsReasonButNoSnapshotAsync()
    {
        SetupTrackingSetting(enabled: false);
        SetupCore(BuildCore());
        SetupFull(BuildFull());
        _csRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(10);

        await _jim.ConnectedSystems.DeleteAsync(1, TestUtilities.GetInitiatedBy(), deleteChangeHistory: false, changeReason: "no tracking");

        Assert.That(_updatedActivity, Is.Not.Null);
        Assert.That(_updatedActivity!.ConfigurationChangeSnapshot, Is.Null, "no snapshot is captured when tracking is disabled");
        Assert.That(_updatedActivity.ChangeReason, Is.EqualTo("no tracking"), "the reason is recorded independently of the snapshot toggle");
    }

    [Test]
    public async Task ExecuteDeletionAsync_WhenTrackingEnabled_CapturesTombstoneSnapshotOnProvidedActivityAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupFull(BuildFull());
        var activity = new Activity { TargetType = ActivityTargetType.ConnectedSystem, TargetOperationType = ActivityTargetOperationType.Delete };

        await _jim.ConnectedSystems.ExecuteDeletionAsync(1, activity, changeReason: null, evaluateMvoDeletionRules: true, deleteChangeHistory: false);

        Assert.That(activity.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"ConnectedSystem\""),
            "the worker deletion path captures the tombstone on the Activity handed to it by the worker");
        _csRepo.Verify(r => r.DeleteConnectedSystemAsync(1, false), Times.Once, "the system is still deleted after the snapshot is captured");
    }

    [Test]
    public async Task DeleteAsync_LargeSystem_CarriesChangeReasonOntoQueuedActivityAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupCore(BuildCore());
        _csRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(5000); // large: queued as background job

        var result = await _jim.ConnectedSystems.DeleteAsync(1, TestUtilities.GetInitiatedBy(), deleteChangeHistory: false, changeReason: "bulk decommission");

        Assert.That(result.Outcome, Is.EqualTo(DeletionOutcome.QueuedAsBackgroundJob));
        var queuedActivity = _createdActivities.SingleOrDefault(a => a.TargetOperationType == ActivityTargetOperationType.Delete);
        Assert.That(queuedActivity, Is.Not.Null, "queuing the deletion creates its tracking Activity");
        Assert.That(queuedActivity!.ChangeReason, Is.EqualTo("bulk decommission"),
            "the reason entered at request time is carried on the queued Activity so it survives to when the worker runs");
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private void SetupCore(ConnectedSystem core) =>
        _csRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(core);

    private void SetupFull(ConnectedSystem full) =>
        _csRepo.Setup(r => r.GetConnectedSystemAsync(1, It.IsAny<bool>())).ReturnsAsync(full);

    private static ConnectedSystem BuildCore() => new()
    {
        Id = 1,
        Name = "HR System",
        Status = ConnectedSystemStatus.Active
    };

    private static ConnectedSystem BuildFull() => new()
    {
        Id = 1,
        Name = "HR System",
        Description = "The authoritative HR feed",
        Status = ConnectedSystemStatus.Active,
        SettingValues = [],
        RunProfiles = [],
        ObjectTypes = [],
        Partitions = []
    };

    private void SetupTrackingSetting(bool enabled) =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = enabled ? "true" : "false"
            });

    private void SetupHashKeySetting() =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                DisplayName = "Configuration change hash key",
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = _protection.Protect(Convert.ToBase64String(new byte[32]))
            });

    /// <summary>A round-trip credential-protection test double using a recognisable encrypted-value prefix.</summary>
    private sealed class FakeProtection : ICredentialProtectionService
    {
        private const string Prefix = "$JIM$v1$";

        public string? Protect(string? plainText) =>
            string.IsNullOrEmpty(plainText) ? plainText : Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public string? Unprotect(string? protectedData) =>
            string.IsNullOrEmpty(protectedData) || !IsProtected(protectedData)
                ? protectedData
                : Encoding.UTF8.GetString(Convert.FromBase64String(protectedData[Prefix.Length..]));

        public bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
