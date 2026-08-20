// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests that a factory reset restores the provenance of the built-in API rate limiting Service Settings
/// (issue #500). Service Setting rows themselves are never truncated by <c>SystemRepository.ResetSystemAsync</c>
/// (they are not customer data), but the Activities table is, which loses the Create Activity and version-1
/// configuration snapshot that show they were created by JIM rather than an administrator. Without
/// <see cref="JIM.Application.Servers.SeedingServer.RebaselineBuiltInConfigurationAsync"/>, that provenance
/// would be permanently lost after a reset.
/// <para>
/// The rebaseline is the one post-wipe step the built-in configuration pipeline cannot replace (issue #916): the
/// ordinary passes no-op for objects the wipe preserved, so only a deliberate re-record restores their history.
/// </para>
/// </summary>
[TestFixture]
public class SystemResetServiceSettingBaselineTests
{
    private static readonly string[] RateLimitingSettingKeys =
    {
        Constants.SettingKeys.RateLimitingEnabled,
        Constants.SettingKeys.RateLimitingAuthenticatedRequestsPerMinute,
        Constants.SettingKeys.RateLimitingUnauthenticatedRequestsPerMinute
    };

    private SeedingTestHarness _harness = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        _harness = new SeedingTestHarness();

        // Reach a genuinely seeded state first, so the Service Settings under test exist with their real values and
        // the reset's passes find nothing to create. Then clear the Activities, which is what the wipe does to them.
        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();
        _harness.CreatedActivities.Clear();
        _harness.UpdatedActivities.Clear();
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    [Test]
    public async Task ResetSystemAsync_RestoresCreateActivityAndVersionOneBaselineForRateLimitingSettingsAsync()
    {
        await _harness.Jim.System.ResetSystemAsync(
            ActivityInitiatorType.ApiKey, Guid.NewGuid(), "Infrastructure Key", includeAdministrators: false);

        foreach (var key in RateLimitingSettingKeys)
        {
            var activity = _harness.CreatedActivities.SingleOrDefault(a =>
                a.TargetType == ActivityTargetType.ServiceSetting && a.ServiceSettingKey == key);

            Assert.That(activity, Is.Not.Null, $"a factory reset must re-record the Create Activity for '{key}'");
            Assert.That(activity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
            Assert.That(activity.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
            Assert.That(activity.ConfigurationChangeVersion, Is.EqualTo(1),
                "the restored baseline is version 1: the reset returned the setting to its factory state");
        }
    }

    [Test]
    public async Task ResetSystemAsync_RateLimitingSettingBaselines_AreGroupedUnderTheSystemInitialisationParentAsync()
    {
        await _harness.Jim.System.ResetSystemAsync(
            ActivityInitiatorType.ApiKey, Guid.NewGuid(), "Infrastructure Key", includeAdministrators: false);

        var parentActivity = _harness.CreatedActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.SystemInitialisation);
        Assert.That(parentActivity, Is.Not.Null, "the reseed must record a System Initialisation parent Activity");

        var rateLimitingActivity = _harness.CreatedActivities.Single(a =>
            a.TargetType == ActivityTargetType.ServiceSetting && a.ServiceSettingKey == Constants.SettingKeys.RateLimitingEnabled);
        Assert.That(rateLimitingActivity.ParentActivityId, Is.EqualTo(parentActivity!.Id));
    }
}
