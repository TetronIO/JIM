// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.LDAP;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.TestSupport;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A Connector setting that belongs to a feature still behind a flag carries the flag's key from the Connector onto the
/// persisted Connector Definition, and is unavailable (hidden from administrators and refused when set) while that flag
/// is off. The LDAP Connector's Exchange settings were the first (#2021); they shipped with the flag's removal (#2681),
/// so they carry none now, and the mechanism is proven against a synthetic flag.
/// </summary>
[TestFixture]
public class ConnectorSettingFeatureFlagTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _mockActivityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<JIM.Models.Activities.Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<JIM.Models.Activities.Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepository.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<JIM.Models.Activities.ActivityTargetType>(), It.IsAny<int>()))
            .ReturnsAsync(0);
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemsAsync()).ReturnsAsync(new List<ConnectedSystem>());
    }

    [Test]
    public void CopyConnectorSettingsToConnectorDefinition_LdapExchangeSettings_CarryNoFeatureFlag()
    {
        using var jim = Application(flagsOn: true);
        var definition = new ConnectorDefinition { Name = ConnectorConstants.LdapConnectorName };

        using var connector = new LdapConnector();
        jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(connector, definition);

        var manage = definition.Settings.Single(s => s.Name == LdapConnectorConstants.SETTING_MANAGE_EXCHANGE_RECIPIENTS);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manage.Type, Is.EqualTo(ConnectedSystemSettingType.CheckBox));
            Assert.That(manage.DefaultCheckboxValue, Is.False);
            Assert.That(manage.FeatureFlagKey, Is.Null);
            Assert.That(definition.Settings.Single(s => s.Name == "Exchange").FeatureFlagKey, Is.Null, "the heading shows with its settings");
        }
    }

    [Test]
    public void CopyConnectorSettingsToConnectorDefinition_LdapExchangeServer_IsRequiredWhileManagingExchange()
    {
        using var jim = Application(flagsOn: true);
        var definition = new ConnectorDefinition { Name = ConnectorConstants.LdapConnectorName };

        using var connector = new LdapConnector();
        jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(connector, definition);

        var server = definition.Settings.Single(s => s.Name == LdapConnectorConstants.SETTING_EXCHANGE_SERVER);
        var manageValue = new ConnectedSystemSettingValue { Setting = definition.Settings.Single(s => s.Name == LdapConnectorConstants.SETTING_MANAGE_EXCHANGE_RECIPIENTS) };
        var serverValue = new ConnectedSystemSettingValue { Setting = server };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(server.Type, Is.EqualTo(ConnectedSystemSettingType.String));
            Assert.That(server.FeatureFlagKey, Is.Null);
            Assert.That(server.Category, Is.EqualTo(ConnectedSystemSettingCategory.Export), "under the Exchange heading");
            Assert.That(ConnectorSettingValidator.IsSettingRequired([manageValue, serverValue], server), Is.False, "not while Exchange is not managed");
            manageValue.CheckboxValue = true;
            Assert.That(ConnectorSettingValidator.IsSettingRequired([manageValue, serverValue], server), Is.True);
        }
    }

    [Test]
    public async Task SyncBuiltInConnectorDefinitions_SettingPersistedWithARetiredFlag_LosesItAsync()
    {
        using var connector = new LdapConnector();
        var settings = connector.GetSettings().Select(s => new ConnectorDefinitionSetting
        {
            Name = s.Name, Type = s.Type, Category = s.Category, Description = s.Description, Example = s.Example,
            Required = s.Required, RequiredGroup = s.RequiredGroup, RequiredGroupCardinality = s.RequiredGroupCardinality,
            RequiredWhenSetting = s.RequiredWhenSetting, RequiredWhenValue = s.RequiredWhenValue,
            DefaultCheckboxValue = s.DefaultCheckboxValue, DefaultStringValue = s.DefaultStringValue,
            DefaultIntValue = s.DefaultIntValue, DropDownValues = s.DropDownValues,
            FeatureFlagKey = s.Name is LdapConnectorConstants.SETTING_MANAGE_EXCHANGE_RECIPIENTS or LdapConnectorConstants.SETTING_EXCHANGE_SERVER
                ? "Features.ExchangeRecipients" // as persisted while the feature was flagged
                : null
        }).ToList();
        var definition = new ConnectorDefinition { Id = 1, Name = ConnectorConstants.LdapConnectorName, BuiltIn = true, Settings = settings };
        _mockConnectedSystemRepository
            .Setup(r => r.GetConnectorDefinitionAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, bool _) => name == ConnectorConstants.LdapConnectorName ? definition : null);
        using var jim = Application(flagsOn: true);

        await jim.Seeding.SyncBuiltInConnectorDefinitionsAsync();

        // Otherwise the setting would stay hidden for good: a key no catalogue entry declares never reads enabled.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(definition.Settings.Single(s => s.Name == LdapConnectorConstants.SETTING_MANAGE_EXCHANGE_RECIPIENTS).FeatureFlagKey, Is.Null);
            Assert.That(definition.Settings.Single(s => s.Name == LdapConnectorConstants.SETTING_EXCHANGE_SERVER).FeatureFlagKey, Is.Null);
        }
    }

    [TestCase(false, false)]
    [TestCase(true, true)]
    public async Task IsSettingAvailableAsync_FlaggedSetting_FollowsItsFlagAsync(bool flagsOn, bool expected)
    {
        using var catalogue = FeatureFlagCatalogueScope.Use(FeatureFlagCatalogueScope.InDevelopmentFlag);
        using var jim = Application(flagsOn);
        var setting = new ConnectorDefinitionSetting { Name = "Flagged", FeatureFlagKey = FeatureFlagCatalogueScope.InDevelopmentFlag.Key };

        Assert.That(await jim.ConnectedSystems.IsSettingAvailableAsync(setting), Is.EqualTo(expected));
    }

    [Test]
    public async Task IsSettingAvailableAsync_UnflaggedSetting_IsAlwaysAvailableAsync()
    {
        using var jim = Application(flagsOn: false);

        Assert.That(await jim.ConnectedSystems.IsSettingAvailableAsync(new ConnectorDefinitionSetting { Name = "Host" }), Is.True);
    }

    private JimApplication Application(bool flagsOn)
    {
        _mockRepository.Setup(r => r.ServiceSettings).Returns(flagsOn
            ? InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled()
            : new InMemoryServiceSettingsRepository());
        return new JimApplication(_mockRepository.Object);
    }
}
