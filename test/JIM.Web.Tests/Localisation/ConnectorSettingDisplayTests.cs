// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Connectors.File;
using JIM.Connectors.Graph;
using JIM.Connectors.LDAP;
using JIM.Connectors.SCIM;
using JIM.Connectors.Sql;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Web.Localisation;
using JIM.Web.Resources;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// Guards the display-time translation of the built-in Connectors' settings (#3030): every setting a built-in Connector
/// declares has its label, description and choices in <c>ConnectorSettingText.resx</c>, and the neutral English equals what
/// the Connector declares, so the portal reads the same in English whichever route the text takes.
/// </summary>
[TestFixture]
public class ConnectorSettingDisplayTests
{
    private static IEnumerable<IConnector> BuiltInConnectors() =>
        [new LdapConnector(), new FileConnector(), new ScimConnector(), new SqlConnector(), new GraphConnector()];

    private static string? Neutral(string key) => ConnectorSettingText.ResourceManager.GetString(key, CultureInfo.InvariantCulture);

    private static IEnumerable<(string Definition, ConnectorSetting Setting)> AllSettings() =>
        BuiltInConnectors().SelectMany(connector => ((IConnectorSettings)connector).GetSettings().Select(setting => (connector.Name, setting)));

    [Test]
    public void EveryBuiltInConnectorSetting_HasALabel_AMatchingDescription_AndEveryChoice()
    {
        var problems = new List<string>();
        foreach (var (definition, setting) in AllSettings())
        {
            var label = Neutral(ConnectorSettingDisplay.KeyFor(definition, setting.Name!, "Label"));
            if (label != setting.Name)
                problems.Add($"{definition} / {setting.Name}: label is \"{label}\"");

            if (!string.IsNullOrEmpty(setting.Description))
            {
                var description = Neutral(ConnectorSettingDisplay.KeyFor(definition, setting.Name!, "Description"));
                if (description != setting.Description)
                    problems.Add($"{definition} / {setting.Name}: description in ConnectorSettingText.resx differs from the Connector's");
            }

            foreach (var option in setting.DropDownValues ?? [])
            {
                var text = Neutral(ConnectorSettingDisplay.KeyFor(definition, setting.Name!, "Option_" + ConnectorSettingDisplay.Slug(option)));
                if (text != option)
                    problems.Add($"{definition} / {setting.Name}: choice \"{option}\" is \"{text}\" in ConnectorSettingText.resx");
            }
        }

        Assert.That(problems, Is.Empty, "Add the setting to ConnectorSettingText (engineering/LOCALISATION.md):" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryBuiltInConnector_HasADescriptionMatchingWhatItDeclares()
    {
        foreach (var connector in BuiltInConnectors())
        {
            var neutral = Neutral(ConnectorSettingDisplay.Slug(connector.Name) + "_ConnectorDescription");
            Assert.That(neutral, Is.EqualTo(connector.Description), connector.Name);
        }
    }

    [Test]
    public void ResourceKeys_AreUniquePerSetting()
    {
        var keys = AllSettings().Select(entry => ConnectorSettingDisplay.KeyFor(entry.Definition, entry.Setting.Name!, "Label")).ToList();
        Assert.That(keys, Is.Unique);
    }

    [Test]
    [SetUICulture("fr")]
    public void Label_InFrench_TranslatesABuiltInSetting_AndKeepsTheStoredNameForAnUnknownOne()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ConnectorSettingDisplay.Label("JIM LDAP Connector", "Port"), Is.EqualTo("Port"));
            Assert.That(ConnectorSettingDisplay.Label("JIM LDAP Connector", "Connection Timeout"), Is.EqualTo("Délai de connexion"));
            Assert.That(ConnectorSettingDisplay.Label("Third Party Connector", "Connection Timeout"), Is.EqualTo("Connection Timeout"));
            Assert.That(ConnectorSettingDisplay.Description("Third Party Connector", "Anything", "Stored text"), Is.EqualTo("Stored text"));
            Assert.That(ConnectorSettingDisplay.Option("JIM File Connector", "Mode", "Bidirectional"), Is.EqualTo("Bidirectionnel"));
        }
    }

    [Test]
    [SetUICulture("en-GB")]
    public void Label_InEnglish_IsTheStoredName()
    {
        Assert.That(ConnectorSettingDisplay.Label("JIM LDAP Connector", "Connection Timeout"), Is.EqualTo("Connection Timeout"));
    }
}
