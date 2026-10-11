// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Models.Core;

namespace JIM.Worker.Tests.Docs;

/// <summary>
/// COV-3 (engineering/DOCUMENTATION_STANDARD.md, "Coverage"; #1454): every Service Setting has a row in the Service
/// Settings table of the Configuration Reference, and every row in it names a Service Setting that exists. The
/// inventory is <see cref="Constants.SettingKeys"/>, read by reflection, so a setting added there is checked the moment
/// it exists; <see cref="Servers.ServiceSettingSeedingCoverageTests"/> holds that each one is also seeded, which is what puts it
/// in front of an administrator. A deliberate exception goes in the ServiceSettings allow-list in
/// docs/.conformance/coverage.psd1, with its reason.
/// </summary>
[TestFixture]
public class ServiceSettingDocsCoverageTests
{
    private const string ReferencePage = "docs/administration/configuration.md";
    private const string AllowListName = "ServiceSettings";

    [Test]
    public void ServiceSettingKeys_EachHasARowInTheConfigurationReference()
    {
        var documented = DocumentedKeys().Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var allowed = DocsCoverageSource.ReadAllowList(AllowListName);

        var undocumented = SettingKeys()
            .Where(k => !documented.Contains(k.Key) && !allowed.ContainsKey(k.Key))
            .Select(k => $"{k.Key} (Constants.SettingKeys.{k.Field})")
            .ToList();

        Assert.That(undocumented, Is.Empty,
            $"COV-3: {undocumented.Count} Service Setting(s) have no row in the Service Settings table of {ReferencePage}. " +
            "Add a row giving the key, display name, category, what it does and its default, or allow-list the key in " +
            "docs/.conformance/coverage.psd1 with the reason it is not documented:\n  " + string.Join("\n  ", undocumented));
    }

    [Test]
    public void ConfigurationReferenceRows_EachNamesAServiceSettingThatExists()
    {
        var keys = SettingKeys().Select(k => k.Key).ToHashSet(StringComparer.Ordinal);

        var unknown = DocumentedKeys()
            .Where(r => !keys.Contains(r.Name))
            .Select(r => $"{ReferencePage}:{r.Line} {r.Name}")
            .ToList();

        Assert.That(unknown, Is.Empty,
            "COV-3: these rows name a Service Setting that Constants.SettingKeys does not have; correct the key, or " +
            "remove the row if the setting is gone:\n  " + string.Join("\n  ", unknown));
    }

    [Test]
    public void ServiceSettingsAllowList_EachEntryGivesAReasonAndStillApplies()
    {
        var keys = SettingKeys().Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
        var documented = DocumentedKeys().Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var (key, reason) in DocsCoverageSource.ReadAllowList(AllowListName))
        {
            if (string.IsNullOrWhiteSpace(reason))
                problems.Add($"{key}: gives no reason; say why it is not documented.");
            else if (!keys.Contains(key))
                problems.Add($"{key}: is not a Service Setting any more; remove the entry.");
            else if (documented.Contains(key))
                problems.Add($"{key}: is documented now; remove the entry.");
        }

        Assert.That(problems, Is.Empty,
            $"COV-3: the {AllowListName} allow-list in docs/.conformance/coverage.psd1 has entries that no longer " +
            "apply:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>Every key in <see cref="Constants.SettingKeys"/>, with the name of the constant that holds it.</summary>
    internal static List<(string Field, string Key)> SettingKeys() =>
        typeof(Constants.SettingKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!))
            .ToList();

    private static List<(string Name, int Line)> DocumentedKeys() =>
        DocsCoverageSource.ReadTableFirstColumn(ReferencePage, "Key");
}
