// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

[TestFixture]
public class ConnectorSettingGroupValidatorTests
{
    private const string ObjectTypeGroup = "Object Type";

    #region Validate Tests

    [Test]
    public void Validate_GroupWithNoValuesSupplied_ReturnsInvalidResult()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Object Type Column", ObjectTypeGroup),
            CreateSettingValue("Object Type", ObjectTypeGroup)
        };

        // Act
        var results = ConnectorSettingGroupValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].IsValid, Is.False);
        Assert.That(results[0].ErrorMessage, Does.Contain("Object Type Column"));
        Assert.That(results[0].ErrorMessage, Does.Contain("Object Type"));
    }

    [Test]
    public void Validate_GroupWithOneStringValueSupplied_ReturnsNoResults()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Object Type Column", ObjectTypeGroup),
            CreateSettingValue("Object Type", ObjectTypeGroup, stringValue: "user")
        };

        // Act
        var results = ConnectorSettingGroupValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Validate_GroupWithOnlyEmptyStringValues_ReturnsInvalidResult()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Object Type Column", ObjectTypeGroup, stringValue: ""),
            CreateSettingValue("Object Type", ObjectTypeGroup, stringValue: "")
        };

        // Act
        var results = ConnectorSettingGroupValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].IsValid, Is.False);
    }

    [Test]
    public void Validate_GroupWithIntValueSupplied_ReturnsNoResults()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Port", "Endpoint", type: ConnectedSystemSettingType.Integer, intValue: 636),
            CreateSettingValue("Connection String", "Endpoint")
        };

        // Act
        var results = ConnectorSettingGroupValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Validate_NoGroupsDefined_ReturnsNoResults()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("File Path", requiredGroup: null),
            CreateSettingValue("Delimiter", requiredGroup: null)
        };

        // Act
        var results = ConnectorSettingGroupValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Validate_MultipleGroups_ValidatesEachGroupIndependently()
    {
        // Arrange: group one is satisfied, group two is not
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Setting A1", "Group One", stringValue: "value"),
            CreateSettingValue("Setting A2", "Group One"),
            CreateSettingValue("Setting B1", "Group Two"),
            CreateSettingValue("Setting B2", "Group Two")
        };

        // Act
        var results = ConnectorSettingGroupValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].ErrorMessage, Does.Contain("Setting B1"));
        Assert.That(results[0].ErrorMessage, Does.Contain("Setting B2"));
        Assert.That(results.All(r => r.ErrorMessage != null && !r.ErrorMessage.Contains("Setting A1")), Is.True);
    }

    #endregion

    #region IsGroupSatisfied Tests

    [Test]
    public void IsGroupSatisfied_NoMemberHasValue_ReturnsFalse()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Object Type Column", ObjectTypeGroup),
            CreateSettingValue("Object Type", ObjectTypeGroup)
        };

        // Act
        var result = ConnectorSettingGroupValidator.IsGroupSatisfied(settingValues, ObjectTypeGroup);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsGroupSatisfied_OneMemberHasValue_ReturnsTrue()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Object Type Column", ObjectTypeGroup, stringValue: "objectClass"),
            CreateSettingValue("Object Type", ObjectTypeGroup)
        };

        // Act
        var result = ConnectorSettingGroupValidator.IsGroupSatisfied(settingValues, ObjectTypeGroup);

        // Assert
        Assert.That(result, Is.True);
    }

    #endregion

    #region HasUserSuppliedValue Tests

    [Test]
    public void HasUserSuppliedValue_WithEmptyStringValue_ReturnsFalse()
    {
        // Arrange
        var settingValue = CreateSettingValue("Object Type", ObjectTypeGroup, stringValue: "");

        // Act & Assert
        Assert.That(settingValue.HasUserSuppliedValue(), Is.False);
    }

    [Test]
    public void HasUserSuppliedValue_WithStringValue_ReturnsTrue()
    {
        // Arrange
        var settingValue = CreateSettingValue("Object Type", ObjectTypeGroup, stringValue: "user");

        // Act & Assert
        Assert.That(settingValue.HasUserSuppliedValue(), Is.True);
    }

    [Test]
    public void HasUserSuppliedValue_WithIntValue_ReturnsTrue()
    {
        // Arrange
        var settingValue = CreateSettingValue("Port", "Endpoint", type: ConnectedSystemSettingType.Integer, intValue: 389);

        // Act & Assert
        Assert.That(settingValue.HasUserSuppliedValue(), Is.True);
    }

    #endregion

    private static ConnectedSystemSettingValue CreateSettingValue(
        string name,
        string? requiredGroup,
        ConnectedSystemSettingType type = ConnectedSystemSettingType.String,
        string? stringValue = null,
        int? intValue = null)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting
            {
                Name = name,
                Type = type,
                RequiredGroup = requiredGroup
            },
            StringValue = stringValue,
            IntValue = intValue
        };
    }
}
