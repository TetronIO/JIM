// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Worker.Tests.Docs;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Every key in <c>Constants.SettingKeys</c> is a Service Setting an administrator can see, which only holds if the
/// built-in configuration pipeline creates it. <c>Worker.StaleTaskTimeout</c> was declared, read by the Scheduler and
/// classified for change capture, but never seeded, so it appeared nowhere and could not be changed (#2060). This
/// fixture runs the pipeline against an empty store, as a first startup does, and fails for any key it did not
/// create, so a setting cannot be added to the constants without reaching administrators. It is the seeding half of
/// COV-3; <see cref="ServiceSettingDocsCoverageTests"/> is the documentation half.
/// </summary>
[TestFixture]
public class ServiceSettingSeedingCoverageTests
{
    /// <summary>
    /// Keys deliberately not created by seeding, each with the reason. An entry that seeding now creates, or that is
    /// no longer a key, fails <see cref="NotSeededExceptions_EachStillAppliesAsync"/>.
    /// </summary>
    private static readonly Dictionary<string, string> NotSeeded = new(StringComparer.Ordinal);

    private SeedingTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new SeedingTestHarness();

    [TearDown]
    public void TearDown() => _harness.Dispose();

    [Test]
    public async Task ApplyBuiltInConfigurationAsync_EmptyStore_CreatesEveryServiceSettingKeyAsync()
    {
        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();

        var notCreated = ServiceSettingDocsCoverageTests.SettingKeys()
            .Where(k => !_harness.Settings.ContainsKey(k.Key) && !NotSeeded.ContainsKey(k.Key))
            .Select(k => $"{k.Key} (Constants.SettingKeys.{k.Field})")
            .ToList();

        Assert.That(notCreated, Is.Empty,
            "These Service Setting keys are never created by the built-in configuration pipeline, so no administrator " +
            "can see or change them. Seed each in SeedingServer.SyncServiceSettingsAsync, or, if it is deliberately " +
            "not a setting, replace the key with a plain constant:\n  " + string.Join("\n  ", notCreated));
    }

    [Test]
    public async Task NotSeededExceptions_EachStillAppliesAsync()
    {
        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();
        var keys = ServiceSettingDocsCoverageTests.SettingKeys().Select(k => k.Key).ToHashSet(StringComparer.Ordinal);

        var stale = NotSeeded.Keys
            .Where(key => !keys.Contains(key) || _harness.Settings.ContainsKey(key))
            .Select(key => keys.Contains(key) ? $"{key}: seeding creates it now" : $"{key}: is not a setting key any more")
            .ToList();

        Assert.That(stale, Is.Empty, "Remove these exceptions from NotSeeded:\n  " + string.Join("\n  ", stale));
    }
}
