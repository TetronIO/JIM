// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Docs;

/// <summary>
/// COV-6 for Connector settings (engineering/DOCUMENTATION_STANDARD.md, "Coverage"; #1454): every setting a built-in
/// Connector offers has a row in a Setting table on that Connector's page, and every row in those tables names a
/// setting the Connector has. The inventory is each Connector's own <see cref="IConnectorSettings.GetSettings"/> over
/// <see cref="SeedingServer.BuiltInConnectors"/>, the list seeding uses, so a new Connector or a new setting is
/// checked the moment it ships. A Connector's page is the one docs/connectors/index.md links under its name. A
/// deliberate exception goes in the ConnectorSettings allow-list in docs/.conformance/coverage.psd1, keyed
/// "Connector name: setting name", with its reason.
/// </summary>
[TestFixture]
public class ConnectorSettingDocsCoverageTests
{
    private const string ConnectorIndex = "docs/connectors/index.md";
    private const string AllowListName = "ConnectorSettings";

    /// <summary>Setting types that lay out the settings form rather than ask for a value; nothing to document.</summary>
    private static readonly ConnectedSystemSettingType[] LayoutTypes =
        [ConnectedSystemSettingType.Heading, ConnectedSystemSettingType.Label, ConnectedSystemSettingType.Divider];

    [Test]
    public void BuiltInConnectors_EachHasAPageLinkedFromTheConnectorIndex()
    {
        var missing = Connectors()
            .Where(c => ConnectorPage(c.Name) == null)
            .Select(c => c.Name)
            .ToList();

        Assert.That(missing, Is.Empty,
            $"COV-6: these Connectors have no row in {ConnectorIndex} linking to their page by name:\n  " +
            string.Join("\n  ", missing));
    }

    [Test]
    public void ConnectorSettings_EachHasARowOnItsConnectorsPage()
    {
        var allowed = DocsCoverageSource.ReadAllowList(AllowListName);
        var undocumented = new List<string>();

        foreach (var connector in Connectors())
        {
            var page = ConnectorPage(connector.Name);
            if (page == null)
                continue;

            var documented = DocumentedSettings(page).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
            undocumented.AddRange(SettingNames(connector)
                .Where(s => !documented.Contains(s) && !allowed.ContainsKey($"{connector.Name}: {s}"))
                .Select(s => $"{page}: {connector.Name}: {s}"));
        }

        Assert.That(undocumented, Is.Empty,
            $"COV-6: {undocumented.Count} Connector setting(s) have no row in a Setting table on their Connector's page. " +
            "Add a row giving the setting's name as the form shows it, or allow-list it in docs/.conformance/coverage.psd1 " +
            "as \"Connector name: setting name\" with the reason it is not documented:\n  " + string.Join("\n  ", undocumented));
    }

    [Test]
    public void ConnectorPageSettingRows_EachNamesASettingTheConnectorHas()
    {
        var unknown = new List<string>();

        foreach (var connector in Connectors())
        {
            var page = ConnectorPage(connector.Name);
            if (page == null)
                continue;

            var settings = SettingNames(connector).ToHashSet(StringComparer.Ordinal);
            unknown.AddRange(DocumentedSettings(page)
                .Where(r => !settings.Contains(r.Name))
                .Select(r => $"{page}:{r.Line} {r.Name}"));
        }

        Assert.That(unknown, Is.Empty,
            "COV-6: these Setting rows name a setting their Connector does not offer; write the name exactly as the " +
            "settings form shows it, or remove the row if the setting is gone:\n  " + string.Join("\n  ", unknown));
    }

    [Test]
    public void ConnectorSettingsAllowList_EachEntryGivesAReasonAndStillApplies()
    {
        var problems = new List<string>();
        var connectors = Connectors().ToDictionary(c => c.Name, StringComparer.Ordinal);

        foreach (var (entry, reason) in DocsCoverageSource.ReadAllowList(AllowListName))
        {
            var separator = entry.IndexOf(": ", StringComparison.Ordinal);
            var connectorName = separator > 0 ? entry[..separator] : entry;
            var settingName = separator > 0 ? entry[(separator + 2)..] : "";

            if (string.IsNullOrWhiteSpace(reason))
                problems.Add($"{entry}: gives no reason; say why it is not documented.");
            else if (!connectors.TryGetValue(connectorName, out var connector) || !SettingNames(connector).Contains(settingName))
                problems.Add($"{entry}: names no setting a built-in Connector offers; remove the entry.");
            else if (ConnectorPage(connectorName) is { } page && DocumentedSettings(page).Any(r => r.Name == settingName))
                problems.Add($"{entry}: is documented now; remove the entry.");
        }

        Assert.That(problems, Is.Empty,
            $"COV-6: the {AllowListName} allow-list in docs/.conformance/coverage.psd1 has entries that no longer " +
            "apply:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// The built-in Connectors an administrator can meet. One behind an In Development feature flag is surfaced nowhere,
    /// so it has no public page until its flag reaches Preview, when this starts requiring one.
    /// </summary>
    private static List<IConnector> Connectors() => SeedingServer.BuiltInConnectors()
        .Where(c => ConnectedSystemServer.FeatureFlagFor(c.Name)?.Tier != FeatureFlagTier.InDevelopment)
        .ToList();

    /// <summary>The settings a Connector asks an administrator for, by the name the settings form shows.</summary>
    private static List<string> SettingNames(IConnector connector) =>
        connector is IConnectorSettings withSettings
            ? withSettings.GetSettings()
                .Where(s => !LayoutTypes.Contains(s.Type) && !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => s.Name!)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>The page docs/connectors/index.md links to under the Connector's name, or null if it links none.</summary>
    private static string? ConnectorPage(string connectorName)
    {
        var index = File.ReadAllText(DocsCoverageSource.FullPath(ConnectorIndex));
        var link = Regex.Match(index, $@"\[{Regex.Escape(connectorName)}\]\((?<page>[^)#]+\.md)\)");
        return link.Success ? $"docs/connectors/{link.Groups["page"].Value}" : null;
    }

    private static List<(string Name, int Line)> DocumentedSettings(string page) =>
        DocsCoverageSource.ReadTableFirstColumn(page, "Setting");
}
