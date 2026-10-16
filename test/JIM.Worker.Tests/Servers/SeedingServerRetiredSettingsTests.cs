// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Four Service Settings were seeded, shown to administrators and, three of them, editable, while nothing acted on
/// them (#2101): <c>Maintenance.IsEnabled</c> paused nothing (the real startup gate is the settings singleton's
/// <c>IsServiceInMaintenanceMode</c>, and a genuine pause is #1619), <c>Security.CredentialEncryptionEnabled</c>
/// switched nothing off, <c>Sync.VerboseNoChangeRecording</c> recorded nothing extra, and
/// <c>SSO.UniqueIdentifierClaimType</c> showed <c>sub</c> whatever claim JIM matched on. They are retired: never
/// created on a new installation, and removed from an existing one the next time it starts.
/// <para>
/// The same fixture holds that seeding converges what it says about a setting (its display name, description and
/// category) on existing installations, where it previously only ever rewrote read-only settings, so a corrected
/// description reaches every installation; and that it never touches an administrator's value in doing so.
/// </para>
/// </summary>
[TestFixture]
public class SeedingServerRetiredSettingsTests
{
    // Literal keys: the constants are gone, and the rows they named are what an existing database still holds.
    private static readonly string[] RetiredKeys =
    [
        "Maintenance.IsEnabled",
        "Security.CredentialEncryptionEnabled",
        "Sync.VerboseNoChangeRecording",
        "SSO.UniqueIdentifierClaimType",
    ];

    private SeedingTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new SeedingTestHarness();

    [TearDown]
    public void TearDown() => _harness.Dispose();

    [Test]
    public async Task ApplyBuiltInConfigurationAsync_EmptyStore_CreatesNoRetiredSettingAsync()
    {
        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();

        Assert.That(RetiredKeys.Where(_harness.Settings.ContainsKey), Is.Empty);
    }

    [Test]
    public async Task ApplyBuiltInConfigurationAsync_ExistingInstallationHoldsThem_RemovesThemAndKeepsTheRestAsync()
    {
        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();
        var before = _harness.Settings.Keys.ToHashSet();
        foreach (var key in RetiredKeys)
            _harness.Settings[key] = new ServiceSetting { Key = key, DisplayName = key, Value = "true" };

        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RetiredKeys.Where(_harness.Settings.ContainsKey), Is.Empty,
                "an installation upgraded from a release that seeded them must lose them on its next start");
            Assert.That(_harness.Settings.Keys, Is.EquivalentTo(before), "no other setting may be touched");
        }
    }

    [Test]
    public async Task ApplyBuiltInConfigurationAsync_EditableSettingWithOutdatedMetadata_ConvergesItAndKeepsTheValueAsync()
    {
        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();
        var seeded = _harness.Settings[Constants.SettingKeys.SyncPageSize];
        var (displayName, description, category) = (seeded.DisplayName, seeded.Description, seeded.Category);
        seeded.DisplayName = "An older name";
        seeded.Description = "An older description";
        seeded.Category = ServiceSettingCategory.UI;
        seeded.Value = "750";

        await _harness.Jim.Seeding.ApplyBuiltInConfigurationAsync();

        var converged = _harness.Settings[Constants.SettingKeys.SyncPageSize];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(converged.DisplayName, Is.EqualTo(displayName));
            Assert.That(converged.Description, Is.EqualTo(description));
            Assert.That(converged.Category, Is.EqualTo(category));
            Assert.That(converged.Value, Is.EqualTo("750"), "an administrator's value is theirs, whatever seeding says");
        }
    }
}
