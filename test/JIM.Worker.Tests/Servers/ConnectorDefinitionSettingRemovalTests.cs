// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Connectors;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers what happens to a Connected System setting that a Connector no longer declares, as happened when the LDAP
/// Connector's Certificate Validation setting was withdrawn (#1132).
/// </summary>
/// <remarks>
/// Detaching the setting from its Connector Definition is not enough. The relationship's foreign key is nullable, so
/// removing the setting from the definition's collection severs it and leaves the row behind, holding no definition
/// but still referenced by every value an administrator had saved against it, which surfaces as a setting that has
/// been withdrawn yet still appears on existing Connected Systems. The setting has to be deleted outright so those
/// values cascade away with it.
/// </remarks>
[TestFixture]
public class ConnectorDefinitionSettingRemovalTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepository = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockServiceSettingsRepository = new Mock<IServiceSettingsRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepository.Object);

        // The drift-sync audits itself, so the Activity and configuration-change plumbing has to answer.
        _mockActivityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<JIM.Models.Activities.Activity>()))
            .Returns(Task.CompletedTask);
        _mockActivityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<JIM.Models.Activities.Activity>()))
            .Returns(Task.CompletedTask);
        _mockActivityRepository.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<JIM.Models.Activities.ActivityTargetType>(), It.IsAny<int>()))
            .ReturnsAsync(0);

        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task SyncBuiltInConnectorDefinitions_WithASettingTheConnectorNoLongerDeclares_DeletesItAsync()
    {
        var obsoleteSetting = new ConnectorDefinitionSetting
        {
            Id = 42,
            Name = "Certificate Validation",
            Type = ConnectedSystemSettingType.DropDown,
            Category = ConnectedSystemSettingCategory.Connectivity
        };

        var definition = new ConnectorDefinition
        {
            Id = 1,
            Name = ConnectorConstants.LdapConnectorName,
            BuiltIn = true,
            Settings = [obsoleteSetting]
        };

        _mockConnectedSystemRepository
            .Setup(r => r.GetConnectorDefinitionAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, bool _) => name == ConnectorConstants.LdapConnectorName ? definition : null);

        List<ConnectorDefinitionSetting>? deleted = null;
        _mockConnectedSystemRepository
            .Setup(r => r.DeleteConnectorDefinitionSettingsAsync(It.IsAny<IList<ConnectorDefinitionSetting>>()))
            .Callback<IList<ConnectorDefinitionSetting>>(settings => deleted = settings.ToList())
            .Returns(Task.CompletedTask);

        await _application.Seeding.SyncBuiltInConnectorDefinitionsAsync();

        Assert.That(deleted, Is.Not.Null, "the withdrawn setting should have been deleted, not just detached from its Connector Definition");
        Assert.That(deleted!.Select(s => s.Name), Does.Contain("Certificate Validation"));
    }

    [Test]
    public async Task SyncBuiltInConnectorDefinitions_WithNoObsoleteSettings_DeletesNothingAsync()
    {
        using var connector = new JIM.Connectors.LDAP.LdapConnector();
        var definition = new ConnectorDefinition
        {
            Id = 1,
            Name = ConnectorConstants.LdapConnectorName,
            BuiltIn = true,
            Settings = connector.GetSettings().Select(s => new ConnectorDefinitionSetting
            {
                Name = s.Name,
                Type = s.Type,
                Category = s.Category,
                Description = s.Description,
                Required = s.Required,
                RequiredGroup = s.RequiredGroup,
                RequiredGroupCardinality = s.RequiredGroupCardinality,
                RequiredWhenSetting = s.RequiredWhenSetting,
                RequiredWhenValue = s.RequiredWhenValue,
                DefaultCheckboxValue = s.DefaultCheckboxValue,
                DefaultStringValue = s.DefaultStringValue,
                DefaultIntValue = s.DefaultIntValue,
                DropDownValues = s.DropDownValues
            }).ToList()
        };

        _mockConnectedSystemRepository
            .Setup(r => r.GetConnectorDefinitionAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, bool _) => name == ConnectorConstants.LdapConnectorName ? definition : null);

        await _application.Seeding.SyncBuiltInConnectorDefinitionsAsync();

        _mockConnectedSystemRepository.Verify(
            r => r.DeleteConnectorDefinitionSettingsAsync(It.IsAny<IList<ConnectorDefinitionSetting>>()),
            Times.Never);
    }
}
