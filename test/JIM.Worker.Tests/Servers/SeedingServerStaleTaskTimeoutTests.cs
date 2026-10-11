// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// <c>Worker.StaleTaskTimeout</c> is how long a processing worker task may go without a heartbeat before the Scheduler
/// treats its Worker as crashed and recovers it. The Scheduler always read it, but nothing seeded it, so it appeared
/// on no surface, could not be changed (updating an unseeded key throws), and every installation ran on the code's
/// five-minute fallback (#2060). These tests hold that it is seeded on a fresh installation and on an existing one,
/// that an administrator's value survives a restart, and that the value the Scheduler reads is the seeded one.
/// </summary>
[TestFixture]
public class SeedingServerStaleTaskTimeoutTests
{
    private SeedingTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new SeedingTestHarness();

    [TearDown]
    public void TearDown() => _harness.Dispose();

    [Test]
    public async Task ApplyBuiltInConfigurationAsync_EmptyStore_SeedsStaleTaskTimeoutWithFiveMinuteDefaultAsync()
    {
        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();

        Assert.That(_harness.Settings.TryGetValue(Constants.SettingKeys.StaleTaskTimeout, out var setting), Is.True,
            "the stale task timeout must be seeded so administrators can see and change it");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(setting!.Category, Is.EqualTo(ServiceSettingCategory.Maintenance));
            Assert.That(setting.ValueType, Is.EqualTo(ServiceSettingValueType.TimeSpan));
            Assert.That(setting.DefaultValue, Is.EqualTo("00:05:00"));
            Assert.That(setting.Value, Is.Null);
            Assert.That(setting.IsReadOnly, Is.False);
        }
    }

    [Test]
    public async Task ApplyBuiltInConfigurationAsync_ExistingInstallationWithoutIt_SeedsStaleTaskTimeoutAsync()
    {
        // an installation from before the fix holds every other setting, and must still gain this one on upgrade
        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();
        _harness.Settings.Remove(Constants.SettingKeys.StaleTaskTimeout);

        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();

        Assert.That(_harness.Settings.ContainsKey(Constants.SettingKeys.StaleTaskTimeout), Is.True);
    }

    [Test]
    public async Task ApplyBuiltInConfigurationAsync_AdministratorValueSet_PreservesItAsync()
    {
        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();
        _harness.Settings[Constants.SettingKeys.StaleTaskTimeout].Value = "00:20:00";

        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();

        Assert.That(_harness.Settings[Constants.SettingKeys.StaleTaskTimeout].Value, Is.EqualTo("00:20:00"));
    }

    [Test]
    public async Task GetStaleTaskTimeoutAsync_AfterSeeding_ReturnsTheSeededDefaultAsync()
    {
        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();

        Assert.That(await _harness.Jim.ServiceSettings.GetStaleTaskTimeoutAsync(), Is.EqualTo(TimeSpan.FromMinutes(5)));
    }

    [Test]
    public async Task GetStaleTaskTimeoutAsync_AdministratorValueSet_ReturnsItAsync()
    {
        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();
        _harness.Settings[Constants.SettingKeys.StaleTaskTimeout].Value = "00:20:00";

        Assert.That(await _harness.Jim.ServiceSettings.GetStaleTaskTimeoutAsync(), Is.EqualTo(TimeSpan.FromMinutes(20)));
    }
}
