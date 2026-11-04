// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.LDAP;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using JIM.TestSupport;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A Connector's settings show in the order the Connector declares them, on every installation. Setting definitions used
/// to keep no order, so the Settings tab sorted them by database id: on an installation that predates a setting, the new
/// setting came last in its section, under whatever heading happened to be there (the LDAP Connector's Exchange server
/// rendered under Group Membership). Each definition now records its position, and the start-up sync keeps it current.
/// </summary>
[TestFixture]
public class ConnectorSettingDisplayOrderTests
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
        _mockRepository.Setup(r => r.ServiceSettings).Returns(InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled());
        _mockActivityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<JIM.Models.Activities.Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<JIM.Models.Activities.Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepository.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<JIM.Models.Activities.ActivityTargetType>(), It.IsAny<int>()))
            .ReturnsAsync(0);
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemsAsync()).ReturnsAsync(new List<ConnectedSystem>());
    }

    [Test]
    public void CopyConnectorSettingsToConnectorDefinition_Always_RecordsEachSettingsPositionAsTheConnectorDeclaresIt()
    {
        using var jim = new JimApplication(_mockRepository.Object);
        using var connector = new LdapConnector();
        var definition = new ConnectorDefinition { Name = ConnectorConstants.LdapConnectorName };

        jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(connector, definition);

        Assert.That(definition.Settings.Select(s => s.DisplayOrder), Is.EqualTo(Enumerable.Range(0, connector.GetSettings().Count)));
    }

    [Test]
    public async Task SyncBuiltInConnectorDefinitions_SettingAddedSinceInstall_TakesItsDeclaredPositionAndTheOthersFollowAsync()
    {
        // An installation from before the Exchange server setting: every other setting persisted with its id, in
        // declared order, and no position recorded.
        using var connector = new LdapConnector();
        var declared = connector.GetSettings();
        var id = 1;
        var settings = declared
            .Where(s => s.Name != LdapConnectorConstants.SETTING_EXCHANGE_SERVER)
            .Select(s => new ConnectorDefinitionSetting
            {
                Id = id++, Name = s.Name, Type = s.Type, Category = s.Category, Description = s.Description, Example = s.Example,
                Required = s.Required, RequiredGroup = s.RequiredGroup, RequiredGroupCardinality = s.RequiredGroupCardinality,
                RequiredWhenSetting = s.RequiredWhenSetting, RequiredWhenValue = s.RequiredWhenValue,
                DefaultCheckboxValue = s.DefaultCheckboxValue, DefaultStringValue = s.DefaultStringValue,
                DefaultIntValue = s.DefaultIntValue, DropDownValues = s.DropDownValues, FeatureFlagKey = s.FeatureFlagKey
            })
            .ToList();
        var definition = new ConnectorDefinition { Id = 1, Name = ConnectorConstants.LdapConnectorName, BuiltIn = true, Settings = settings };
        _mockConnectedSystemRepository
            .Setup(r => r.GetConnectorDefinitionAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, bool _) => name == ConnectorConstants.LdapConnectorName ? definition : null);
        using var jim = new JimApplication(_mockRepository.Object);

        await jim.Seeding.SyncBuiltInConnectorDefinitionsAsync();

        var inDisplayOrder = definition.Settings.OrderBy(s => s.DisplayOrder).Select(s => s.Name);
        Assert.That(inDisplayOrder, Is.EqualTo(declared.Select(s => s.Name)),
            "Exchange server sits straight after Manage Exchange recipients, under its own heading, whatever its id");
    }

    [Test]
    public async Task SyncBuiltInConnectorDefinitions_PositionsAlreadyCurrent_ChangesNothingAsync()
    {
        using var jim = new JimApplication(_mockRepository.Object);
        using var connector = new LdapConnector();
        var definition = new ConnectorDefinition { Id = 1, Name = ConnectorConstants.LdapConnectorName, BuiltIn = true };
        jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(connector, definition);
        _mockConnectedSystemRepository
            .Setup(r => r.GetConnectorDefinitionAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, bool _) => name == ConnectorConstants.LdapConnectorName ? definition : null);

        // The first start converges what the copy does not carry (the Connector's capability flags); the second has
        // nothing left to write, positions included.
        await jim.Seeding.SyncBuiltInConnectorDefinitionsAsync();
        _mockConnectedSystemRepository.Invocations.Clear();
        _mockConnectedSystemRepository
            .Setup(r => r.GetConnectorDefinitionAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, bool _) => name == ConnectorConstants.LdapConnectorName ? definition : null);

        await jim.Seeding.SyncBuiltInConnectorDefinitionsAsync();

        _mockConnectedSystemRepository.Verify(r => r.UpdateConnectorDefinitionAsync(definition), Times.Never,
            "a converged definition is not written again at every start");
    }
}
