// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests the type-aware retention orchestration: the cleanup applies the general retention cutoff to sync/identity
/// Activities and a separate (typically much longer) cutoff to configuration-change Activities, reports both counts,
/// and the configuration retention period setting defaults to ten years with a zero-value guard.
/// </summary>
[TestFixture]
public class ChangeHistoryRetentionServerTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IChangeHistoryRepository> _changeHistoryRepo = null!;
    private Mock<ISyncRepository> _syncRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _changeHistoryRepo = new Mock<IChangeHistoryRepository>();
        _syncRepo = new Mock<ISyncRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.ChangeHistory).Returns(_changeHistoryRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        // The cleanup now trims initial-password work records too, which reach the database through the sync
        // repository rather than the change-history one.
        _jim = new JimApplication(_repo.Object, syncRepository: _syncRepo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task DeleteExpiredChangeHistoryAsync_AppliesSeparateConfigurationCutoffAndReportsCountsAsync()
    {
        var cutoffs = BuildCutoffs();
        _changeHistoryRepo.Setup(r => r.DeleteExpiredCsoChangesAsync(cutoffs.General, 100)).ReturnsAsync(5);
        _changeHistoryRepo.Setup(r => r.DeleteExpiredMvoChangesAsync(cutoffs.General, 100)).ReturnsAsync(4);
        _changeHistoryRepo.Setup(r => r.DeleteExpiredActivitiesAsync(cutoffs.General, 100)).ReturnsAsync(3);
        _changeHistoryRepo.Setup(r => r.DeleteExpiredConfigurationChangeActivitiesAsync(cutoffs.ConfigurationChange, 100)).ReturnsAsync(2);
        _changeHistoryRepo.Setup(r => r.DeleteExpiredSecurityEventActivitiesAsync(cutoffs.SecurityEvent, 100)).ReturnsAsync(7);
        _changeHistoryRepo.Setup(r => r.DeleteExpiredPasswordEventActivitiesAsync(cutoffs.PasswordEvent, 100)).ReturnsAsync(11);
        _syncRepo.Setup(r => r.DeleteTerminalInitialPasswordsAsync(cutoffs.InitialPassword, 100)).ReturnsAsync(9);
        _syncRepo.Setup(r => r.DeleteTerminalPasswordChangesAsync(cutoffs.PasswordEvent, 100)).ReturnsAsync(13);

        var result = await _jim.ChangeHistory.DeleteExpiredChangeHistoryAsync(cutoffs);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ActivitiesDeleted, Is.EqualTo(3));
            Assert.That(result.ConfigurationChangeActivitiesDeleted, Is.EqualTo(2));
            Assert.That(result.SecurityEventActivitiesDeleted, Is.EqualTo(7));
            Assert.That(result.InitialPasswordWorkRecordsDeleted, Is.EqualTo(9));
            Assert.That(result.PasswordEventActivitiesDeleted, Is.EqualTo(11));
            Assert.That(result.PasswordQueueRecordsDeleted, Is.EqualTo(13));
        }

        _syncRepo.Verify(r => r.DeleteTerminalInitialPasswordsAsync(cutoffs.InitialPassword, 100), Times.Once,
            "initial-password work records are trimmed at their own cutoff, not the general one");
        _changeHistoryRepo.Verify(r => r.DeleteExpiredActivitiesAsync(cutoffs.General, 100), Times.Once,
            "general Activities are flushed at the general retention cutoff");
        _changeHistoryRepo.Verify(r => r.DeleteExpiredConfigurationChangeActivitiesAsync(cutoffs.ConfigurationChange, 100), Times.Once,
            "configuration-change Activities are flushed only at their own, longer cutoff");
        _changeHistoryRepo.Verify(r => r.DeleteExpiredSecurityEventActivitiesAsync(cutoffs.SecurityEvent, 100), Times.Once,
            "security event Activities are flushed only at their own, dedicated cutoff");
        _changeHistoryRepo.Verify(r => r.DeleteExpiredPasswordEventActivitiesAsync(cutoffs.PasswordEvent, 100), Times.Once,
            "Password Synchronisation Activities are flushed only at their own, dedicated cutoff");
        _syncRepo.Verify(r => r.DeleteTerminalPasswordChangesAsync(cutoffs.PasswordEvent, 100), Times.Once,
            "terminal password changes are trimmed under the Password Synchronisation cutoff, alongside their Activities");
    }

    [Test]
    public async Task DeleteExpiredChangeHistoryAsync_ClearsPreviewResultsBeforeTheActivitiesTheyHangOffAsync()
    {
        // Preview results cascade from their Activity, so deleting the Activity would remove them anyway. The
        // problem is volume: the batch limit bounds Activities, and one preview Activity can own hundreds of
        // thousands of delta rows, so a hundred of them cascade in a single transaction. Clearing the results
        // first, bounded by the same limit, keeps each statement the size the limit was chosen for.
        var cutoffs = BuildCutoffs();
        var sequence = new List<string>();
        _changeHistoryRepo.Setup(r => r.DeleteExpiredPreviewsAsync(cutoffs.General, 100))
            .Callback(() => sequence.Add("previews")).ReturnsAsync(6);
        _changeHistoryRepo.Setup(r => r.DeleteExpiredActivitiesAsync(cutoffs.General, 100))
            .Callback(() => sequence.Add("activities")).ReturnsAsync(3);

        var result = await _jim.ChangeHistory.DeleteExpiredChangeHistoryAsync(cutoffs);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.PreviewsDeleted, Is.EqualTo(6),
                "housekeeping that removes preview data without reporting it leaves nobody able to explain the storage drop");
            Assert.That(sequence, Is.EqualTo(new[] { "previews", "activities" }));
        }
    }

    [Test]
    public async Task DeleteExpiredChangeHistoryAsync_CallerOwnedActivity_IsLeftOpenForTheCallerToCompleteAsync()
    {
        // The scheduled step's Activity belongs to the worker task pipeline, which completes it once the pass's
        // summary statistics are on it. Completing it here would close it before those numbers were recorded.
        var activity = new Activity { Id = Guid.NewGuid(), TargetType = ActivityTargetType.HistoryRetentionCleanup };

        var result = await _jim.ChangeHistory.DeleteExpiredChangeHistoryAsync(activity, BuildCutoffs());

        Assert.That(result, Is.Not.Null);
        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never,
            "the caller already created the Activity; a second one would split one pass across two records");
        _activityRepo.Verify(r => r.UpdateActivityAsync(It.IsAny<Activity>()), Times.Never,
            "the caller completes its own Activity, after attaching the summary statistics");
    }

    [Test]
    public async Task DeleteExpiredChangeHistoryAsync_CallerOwnedActivityAndCleanupFails_DoesNotRecordTheFailureItselfAsync()
    {
        // Same ownership rule on the failure path: the caller's handler fails the Activity, so failing it here
        // as well would record the same failure twice against one pass.
        var activity = new Activity { Id = Guid.NewGuid(), TargetType = ActivityTargetType.HistoryRetentionCleanup };
        _changeHistoryRepo.Setup(r => r.DeleteExpiredCsoChangesAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("the database went away"));

        Assert.That(async () => await _jim.ChangeHistory.DeleteExpiredChangeHistoryAsync(activity, BuildCutoffs()),
            Throws.InstanceOf<InvalidOperationException>(),
            "the caller cannot fail an Activity for a failure it never sees");
        _activityRepo.Verify(r => r.UpdateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }

    [Test]
    public async Task GetRetentionCutoffsAsync_DerivesEveryCutoffFromItsOwnRetentionPeriodAsync()
    {
        // One place derives every cutoff, so the scheduled step and the API endpoint cannot disagree about what
        // is eligible. Each period is read from its own setting: a transposed pair here would trim one class of
        // record under another's period, silently.
        StoreTimeSpanSetting(Constants.SettingKeys.HistoryRetentionPeriod, TimeSpan.FromDays(10));
        StoreTimeSpanSetting(Constants.SettingKeys.ConfigurationChangeRetentionPeriod, TimeSpan.FromDays(20));
        StoreTimeSpanSetting(Constants.SettingKeys.SecurityEventRetentionPeriod, TimeSpan.FromDays(30));
        StoreTimeSpanSetting(Constants.SettingKeys.InitialPasswordRetentionPeriod, TimeSpan.FromDays(40));
        StoreTimeSpanSetting(Constants.SettingKeys.PasswordEventRetentionPeriod, TimeSpan.FromDays(50));
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.HistoryCleanupBatchSize))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.HistoryCleanupBatchSize,
                DisplayName = "History cleanup batch size",
                ValueType = ServiceSettingValueType.Integer,
                Value = "250"
            });

        var asOf = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var cutoffs = await _jim.ChangeHistory.GetRetentionCutoffsAsync(asOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cutoffs.General, Is.EqualTo(asOf.AddDays(-10)));
            Assert.That(cutoffs.ConfigurationChange, Is.EqualTo(asOf.AddDays(-20)));
            Assert.That(cutoffs.SecurityEvent, Is.EqualTo(asOf.AddDays(-30)));
            Assert.That(cutoffs.InitialPassword, Is.EqualTo(asOf.AddDays(-40)));
            Assert.That(cutoffs.PasswordEvent, Is.EqualTo(asOf.AddDays(-50)));
            Assert.That(cutoffs.MaxRecordsPerType, Is.EqualTo(250),
                "the shared cleanup batch size bounds every trim in the pass (requirement 30)");
        }
    }

    [Test]
    public async Task GetPasswordEventRetentionPeriodAsync_NoSettingStored_DefaultsToOneYearAsync()
    {
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.PasswordEventRetentionPeriod))
            .ReturnsAsync((ServiceSetting?)null);

        var period = await _jim.ServiceSettings.GetPasswordEventRetentionPeriodAsync();

        Assert.That(period, Is.EqualTo(TimeSpan.FromDays(365)));
    }

    [Test]
    public async Task GetPasswordEventRetentionPeriodAsync_ZeroConfigured_FallsBackToDefaultAsync()
    {
        StoreTimeSpanSetting(Constants.SettingKeys.PasswordEventRetentionPeriod, TimeSpan.Zero);

        var period = await _jim.ServiceSettings.GetPasswordEventRetentionPeriodAsync();

        Assert.That(period, Is.EqualTo(TimeSpan.FromDays(365)),
            "a zero or negative retention period would remove a password change's history the moment it stopped " +
            "being owed, which is the silent divergence Password Synchronisation exists to prevent");
    }

    [Test]
    public async Task GetSecurityEventRetentionPeriodAsync_NoSettingStored_DefaultsToOneYearAsync()
    {
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.SecurityEventRetentionPeriod))
            .ReturnsAsync((ServiceSetting?)null);

        var period = await _jim.ServiceSettings.GetSecurityEventRetentionPeriodAsync();

        Assert.That(period, Is.EqualTo(TimeSpan.FromDays(365)));
    }

    [Test]
    public async Task GetSecurityEventRetentionPeriodAsync_ZeroConfigured_FallsBackToDefaultAsync()
    {
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.SecurityEventRetentionPeriod))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.SecurityEventRetentionPeriod,
                DisplayName = "Security event retention period",
                ValueType = ServiceSettingValueType.TimeSpan,
                Value = "00:00:00"
            });

        var period = await _jim.ServiceSettings.GetSecurityEventRetentionPeriodAsync();

        Assert.That(period, Is.EqualTo(TimeSpan.FromDays(365)),
            "a zero or negative retention period would delete all security event history and must be rejected");
    }

    [Test]
    public async Task GetConfigurationChangeRetentionPeriodAsync_NoSettingStored_DefaultsToTenYearsAsync()
    {
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeRetentionPeriod))
            .ReturnsAsync((ServiceSetting?)null);

        var period = await _jim.ServiceSettings.GetConfigurationChangeRetentionPeriodAsync();

        Assert.That(period, Is.EqualTo(TimeSpan.FromDays(3650)));
    }

    [Test]
    public async Task GetConfigurationChangeRetentionPeriodAsync_ZeroConfigured_FallsBackToDefaultAsync()
    {
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeRetentionPeriod))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeRetentionPeriod,
                DisplayName = "Configuration change retention period",
                ValueType = ServiceSettingValueType.TimeSpan,
                Value = "00:00:00"
            });

        var period = await _jim.ServiceSettings.GetConfigurationChangeRetentionPeriodAsync();

        Assert.That(period, Is.EqualTo(TimeSpan.FromDays(3650)),
            "a zero or negative retention period would delete all configuration history and must be rejected");
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A cutoff set with a distinct period per class, so a test that transposed two of them would fail rather
    /// than pass on coincidentally equal dates.
    /// </summary>
    private static ChangeHistoryRetentionCutoffs BuildCutoffs() => new()
    {
        General = DateTime.UtcNow.AddDays(-90),
        ConfigurationChange = DateTime.UtcNow.AddDays(-3650),
        SecurityEvent = DateTime.UtcNow.AddDays(-365),
        InitialPassword = DateTime.UtcNow.AddDays(-120),
        PasswordEvent = DateTime.UtcNow.AddDays(-200),
        MaxRecordsPerType = 100
    };

    private void StoreTimeSpanSetting(string key, TimeSpan value) =>
        _settingsRepo.Setup(r => r.GetSettingAsync(key))
            .ReturnsAsync(new ServiceSetting
            {
                Key = key,
                DisplayName = key,
                ValueType = ServiceSettingValueType.TimeSpan,
                Value = value.ToString()
            });
}
