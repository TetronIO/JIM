// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What configuration change capture reports back to its caller (#2615). A save that must set a synchronisation in
/// motion cannot read the Activity for this: the Activity's class stays unset for a create and for a capture that did
/// not run, and "tracking is off" or "the capture failed" leave no trace on it at all. The outcome says which of the
/// five situations the capture was in, so the caller can tell "nothing changed" from "JIM could not tell".
/// </summary>
[TestFixture]
public class ConfigurationChangeCaptureOutcomeTests
{
    private static readonly byte[] HashKey = new byte[32];

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>())).ReturnsAsync(6);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object, syncRepository: TestUtilities.QueuedChangeWithdrawalSyncRepository()) { CredentialProtection = _protection };

        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task CaptureChangeAsync_TrackingDisabled_ReportsNotTrackedAsync()
    {
        SetupTrackingSetting(enabled: false);

        var outcome = await CaptureAsync(BuildRule());

        Assert.That(outcome.Kind, Is.EqualTo(ConfigurationChangeCaptureKind.NotTracked));
    }

    [Test]
    public async Task CaptureChangeAsync_SnapshotBuilderReturnsNull_ReportsFailedAsync()
    {
        var activity = new Activity();

        var outcome = await _jim.ConfigurationChangeCapture.CaptureChangeAsync(activity, null,
            ActivityTargetType.SynchronisationRule, 55, _ => Task.FromResult<ConfigurationSnapshot?>(null), "Synchronisation Rule 55");

        Assert.That(outcome.Kind, Is.EqualTo(ConfigurationChangeCaptureKind.Failed), "no snapshot means the change could not be compared");
    }

    [Test]
    public async Task CaptureChangeAsync_CaptureThrowsASwallowedException_ReportsFailedAsync()
    {
        // The capture is best-effort: a failure while building the snapshot never fails the save, and is swallowed.
        // The outcome is how the caller learns it happened.
        var activity = new Activity();

        var outcome = await _jim.ConfigurationChangeCapture.CaptureChangeAsync(activity, null,
            ActivityTargetType.SynchronisationRule, 55, _ => throw new InvalidOperationException("snapshot build failed"), "Synchronisation Rule 55");

        Assert.That(outcome.Kind, Is.EqualTo(ConfigurationChangeCaptureKind.Failed));
    }

    [Test]
    public async Task CaptureChangeAsync_NoEarlierSnapshot_ReportsCreatedAndRecordsTheFirstVersionAsync()
    {
        var activity = new Activity();

        var outcome = await CaptureAsync(BuildRule(), activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Kind, Is.EqualTo(ConfigurationChangeCaptureKind.Created));
            Assert.That(outcome.Class, Is.EqualTo(ConfigurationChangeClass.NotClassified));
            Assert.That(activity.ConfigurationChangeVersion, Is.EqualTo(7), "the capture still records the version");
        }
    }

    [Test]
    public async Task CaptureChangeAsync_IdenticalToTheLatestSnapshot_ReportsNoChangeAsync()
    {
        var rule = BuildRule();
        SetupLatestSnapshot(rule);
        var activity = new Activity();

        var outcome = await CaptureAsync(rule, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Kind, Is.EqualTo(ConfigurationChangeCaptureKind.NoChange));
            Assert.That(activity.ConfigurationChangeVersion, Is.Null, "an unchanged save consumes no version");
        }
    }

    [Test]
    public async Task CaptureChangeAsync_OnlyTheNameChanged_ReportsCosmeticAndNamesTheSectionAsync()
    {
        SetupLatestSnapshot(BuildRule());
        var renamed = BuildRule();
        renamed.Name = "A different name";

        var outcome = await CaptureAsync(renamed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Kind, Is.EqualTo(ConfigurationChangeCaptureKind.Changed));
            Assert.That(outcome.Class, Is.EqualTo(ConfigurationChangeClass.Cosmetic));
            Assert.That(outcome.ChangedSections, Is.EqualTo(new[] { "Name" }));
        }
    }

    [Test]
    public async Task CaptureChangeAsync_EnabledToggled_ReportsSyncAffectingAndNamesTheSectionAsync()
    {
        SetupLatestSnapshot(BuildRule());
        var disabled = BuildRule();
        disabled.Enabled = false;

        var outcome = await CaptureAsync(disabled);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Kind, Is.EqualTo(ConfigurationChangeCaptureKind.Changed));
            Assert.That(outcome.Class, Is.EqualTo(ConfigurationChangeClass.SyncAffecting));
            Assert.That(outcome.ChangedSections, Is.EqualTo(new[] { "Enabled" }));
        }
    }

    [Test]
    public async Task CaptureChangeAsync_SeveralSectionsChanged_ReportsTheHighestClassAndEveryChangedSectionAsync()
    {
        SetupLatestSnapshot(BuildRule());
        var edited = BuildRule();
        edited.Name = "A different name";
        edited.Enabled = false;

        var outcome = await CaptureAsync(edited);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Class, Is.EqualTo(ConfigurationChangeClass.SyncAffecting), "classified by the most consequential change");
            Assert.That(outcome.ChangedSections, Is.EquivalentTo(new[] { "Name", "Enabled" }));
        }
    }

    [Test]
    public async Task CaptureChangeAsync_ClassificationFails_ReportsChangedWithoutAClassAsync()
    {
        // A property with no classification degrades to NotClassified rather than losing the snapshot (the
        // completeness tests stop it reaching a deployment). The outcome must not pretend it is Cosmetic.
        var latest = BuildRule();
        SetupLatestSnapshot(latest);
        var edited = BuildRule();
        edited.Enabled = false;
        var snapshot = _jim.ConfigurationSnapshots.CreateSnapshot(edited, HashKey);
        // An unknown key, so the classifier has nothing to say about it.
        snapshot.Root.Children!.Add(ConfigurationSnapshotNode.Scalar("aPropertyNoClassifierKnows", "1", "Unknown property"));

        var outcome = await _jim.ConfigurationChangeCapture.CaptureChangeAsync(new Activity(), null,
            ActivityTargetType.SynchronisationRule, 55, _ => Task.FromResult<ConfigurationSnapshot?>(snapshot), "Synchronisation Rule 55");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Kind, Is.EqualTo(ConfigurationChangeCaptureKind.Changed));
            Assert.That(outcome.Class, Is.EqualTo(ConfigurationChangeClass.NotClassified));
        }
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private async Task<ConfigurationChangeCaptureOutcome> CaptureAsync(SyncRule rule, Activity? activity = null) =>
        await _jim.ConfigurationChangeCapture.CaptureChangeAsync(activity ?? new Activity(), null,
            ActivityTargetType.SynchronisationRule, rule.Id,
            hashKey => Task.FromResult<ConfigurationSnapshot?>(_jim.ConfigurationSnapshots.CreateSnapshot(rule, hashKey)),
            $"Synchronisation Rule {rule.Id}");

    private void SetupLatestSnapshot(SyncRule rule) =>
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SynchronisationRule, rule.Id))
            .ReturnsAsync(ConfigurationSnapshotService.Serialise(_jim.ConfigurationSnapshots.CreateSnapshot(rule, HashKey)));

    private static SyncRule BuildRule() => new()
    {
        Id = 55,
        Name = "HR Import",
        Direction = SyncRuleDirection.Import,
        ConnectedSystem = new ConnectedSystem { Id = 3, Name = "HR" },
        ConnectedSystemId = 3,
        ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 7, Name = "user" },
        ConnectedSystemObjectTypeId = 7,
        MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person" },
        MetaverseObjectTypeId = 1
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
                Value = _protection.Protect(Convert.ToBase64String(HashKey))
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
