// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

[TestFixture]
public class ConnectorSettingValidatorTests
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
        var results = ConnectorSettingValidator.Validate(settingValues);

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
        var results = ConnectorSettingValidator.Validate(settingValues);

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
        var results = ConnectorSettingValidator.Validate(settingValues);

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
        var results = ConnectorSettingValidator.Validate(settingValues);

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
        var results = ConnectorSettingValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Validate_AtLeastOneGroupWithBothValuesSupplied_ReturnsNoResults()
    {
        // Arrange: default cardinality (AtLeastOne) permits more than one member to have a value
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Object Type Column", ObjectTypeGroup, stringValue: "objectClass"),
            CreateSettingValue("Object Type", ObjectTypeGroup, stringValue: "user")
        };

        // Act
        var results = ConnectorSettingValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Validate_ExactlyOneGroupWithBothValuesSupplied_ReturnsInvalidResult()
    {
        // Arrange: mutually exclusive group rejects supplying more than one value (#792)
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Object Type Column", ObjectTypeGroup, stringValue: "objectClass", cardinality: ConnectorSettingRequiredGroupCardinality.ExactlyOne),
            CreateSettingValue("Object Type", ObjectTypeGroup, stringValue: "user", cardinality: ConnectorSettingRequiredGroupCardinality.ExactlyOne)
        };

        // Act
        var results = ConnectorSettingValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].IsValid, Is.False);
        Assert.That(results[0].ErrorMessage, Does.Contain("only one").IgnoreCase);
        Assert.That(results[0].ErrorMessage, Does.Contain("Object Type Column"));
        Assert.That(results[0].ErrorMessage, Does.Contain("Object Type"));
    }

    [Test]
    public void Validate_ExactlyOneGroupWithOneValueSupplied_ReturnsNoResults()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Object Type Column", ObjectTypeGroup, cardinality: ConnectorSettingRequiredGroupCardinality.ExactlyOne),
            CreateSettingValue("Object Type", ObjectTypeGroup, stringValue: "user", cardinality: ConnectorSettingRequiredGroupCardinality.ExactlyOne)
        };

        // Act
        var results = ConnectorSettingValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Validate_ExactlyOneGroupWithNoValuesSupplied_ReturnsInvalidResultMentioningExactlyOne()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Object Type Column", ObjectTypeGroup, cardinality: ConnectorSettingRequiredGroupCardinality.ExactlyOne),
            CreateSettingValue("Object Type", ObjectTypeGroup, cardinality: ConnectorSettingRequiredGroupCardinality.ExactlyOne)
        };

        // Act
        var results = ConnectorSettingValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].IsValid, Is.False);
        Assert.That(results[0].ErrorMessage, Does.Contain("exactly one").IgnoreCase);
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
        var results = ConnectorSettingValidator.Validate(settingValues);

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
        var result = ConnectorSettingValidator.IsGroupSatisfied(settingValues, ObjectTypeGroup);

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
        var result = ConnectorSettingValidator.IsGroupSatisfied(settingValues, ObjectTypeGroup);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsGroupSatisfied_ExactlyOneGroupWithBothValues_ReturnsFalse()
    {
        // Arrange: a mutually exclusive group is not satisfied when more than one value is supplied
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Object Type Column", ObjectTypeGroup, stringValue: "objectClass", cardinality: ConnectorSettingRequiredGroupCardinality.ExactlyOne),
            CreateSettingValue("Object Type", ObjectTypeGroup, stringValue: "user", cardinality: ConnectorSettingRequiredGroupCardinality.ExactlyOne)
        };

        // Act
        var result = ConnectorSettingValidator.IsGroupSatisfied(settingValues, ObjectTypeGroup);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsGroupSatisfied_ExactlyOneGroupWithOneValue_ReturnsTrue()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Object Type Column", ObjectTypeGroup, cardinality: ConnectorSettingRequiredGroupCardinality.ExactlyOne),
            CreateSettingValue("Object Type", ObjectTypeGroup, stringValue: "user", cardinality: ConnectorSettingRequiredGroupCardinality.ExactlyOne)
        };

        // Act
        var result = ConnectorSettingValidator.IsGroupSatisfied(settingValues, ObjectTypeGroup);

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

    #region Required-value Tests

    [Test]
    public void Validate_RequiredStringSettingMissing_ReturnsInvalidResult()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Directory Server", requiredGroup: null, required: true)
        };

        // Act
        var results = ConnectorSettingValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].IsValid, Is.False);
        Assert.That(results[0].ErrorMessage, Does.Contain("Directory Server"));
        Assert.That(results[0].SettingValue, Is.SameAs(settingValues[0]));
    }

    [Test]
    public void Validate_RequiredIntegerSettingMissing_ReturnsInvalidResult()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Port", requiredGroup: null, type: ConnectedSystemSettingType.Integer, required: true)
        };

        // Act
        var results = ConnectorSettingValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].IsValid, Is.False);
    }

    [Test]
    public void Validate_RequiredSettingSupplied_ReturnsNoResults()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Directory Server", requiredGroup: null, required: true, stringValue: "dc01.corp.local")
        };

        // Act
        var results = ConnectorSettingValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Validate_OptionalSettingMissing_ReturnsNoResults()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Search Timeout", requiredGroup: null, required: false)
        };

        // Act
        var results = ConnectorSettingValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Is.Empty);
    }

    #endregion

    #region RequiredWhen Tests

    [Test]
    public void Validate_RequiredWhenConditionMetAndValueMissing_ReturnsInvalidResult()
    {
        // Arrange: Certificate Validation is required when Use Secure Connection is enabled
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Use Secure Connection", requiredGroup: null, type: ConnectedSystemSettingType.CheckBox, checkboxValue: true),
            CreateSettingValue("Certificate Validation", requiredGroup: null, type: ConnectedSystemSettingType.DropDown,
                requiredWhenSetting: "Use Secure Connection", requiredWhenValue: "true")
        };

        // Act
        var results = ConnectorSettingValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].ErrorMessage, Does.Contain("Certificate Validation"));
    }

    [Test]
    public void Validate_RequiredWhenConditionMetAndValueSupplied_ReturnsNoResults()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Use Secure Connection", requiredGroup: null, type: ConnectedSystemSettingType.CheckBox, checkboxValue: true),
            CreateSettingValue("Certificate Validation", requiredGroup: null, type: ConnectedSystemSettingType.DropDown, stringValue: "Full Validation",
                requiredWhenSetting: "Use Secure Connection", requiredWhenValue: "true")
        };

        // Act
        var results = ConnectorSettingValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Validate_RequiredWhenConditionNotMetAndValueMissing_ReturnsNoResults()
    {
        // Arrange: Use Secure Connection is off, so Certificate Validation is irrelevant and not required
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Use Secure Connection", requiredGroup: null, type: ConnectedSystemSettingType.CheckBox, checkboxValue: false),
            CreateSettingValue("Certificate Validation", requiredGroup: null, type: ConnectedSystemSettingType.DropDown,
                requiredWhenSetting: "Use Secure Connection", requiredWhenValue: "true")
        };

        // Act
        var results = ConnectorSettingValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Validate_RequiredWhenDropDownConditionMet_RequiresDependentSetting()
    {
        // Arrange: Disable Attribute is required when Delete Behaviour is 'Disable'
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Delete Behaviour", requiredGroup: null, type: ConnectedSystemSettingType.DropDown, stringValue: "Disable"),
            CreateSettingValue("Disable Attribute", requiredGroup: null, type: ConnectedSystemSettingType.String,
                requiredWhenSetting: "Delete Behaviour", requiredWhenValue: "Disable")
        };

        // Act
        var results = ConnectorSettingValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].ErrorMessage, Does.Contain("Disable Attribute"));
    }

    [Test]
    public void Validate_RequiredWhenDropDownConditionNotMet_DoesNotRequireDependentSetting()
    {
        // Arrange: Delete Behaviour is 'Delete', so Disable Attribute is irrelevant
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Delete Behaviour", requiredGroup: null, type: ConnectedSystemSettingType.DropDown, stringValue: "Delete"),
            CreateSettingValue("Disable Attribute", requiredGroup: null, type: ConnectedSystemSettingType.String,
                requiredWhenSetting: "Delete Behaviour", requiredWhenValue: "Disable")
        };

        // Act
        var results = ConnectorSettingValidator.Validate(settingValues);

        // Assert
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void IsConditionMet_NoRequiredWhen_ReturnsTrue()
    {
        // Arrange
        var setting = new ConnectorDefinitionSetting { Name = "Search Timeout", Type = ConnectedSystemSettingType.Integer };
        var settingValues = new List<ConnectedSystemSettingValue> { new() { Setting = setting } };

        // Act & Assert
        Assert.That(ConnectorSettingValidator.IsConditionMet(settingValues, setting), Is.True);
    }

    [Test]
    public void IsConditionMet_CheckboxControllerEnabled_ReturnsTrue()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Use Secure Connection", requiredGroup: null, type: ConnectedSystemSettingType.CheckBox, checkboxValue: true),
            CreateSettingValue("Certificate Validation", requiredGroup: null, type: ConnectedSystemSettingType.DropDown,
                requiredWhenSetting: "Use Secure Connection", requiredWhenValue: "true")
        };

        // Act & Assert
        Assert.That(ConnectorSettingValidator.IsConditionMet(settingValues, settingValues[1].Setting), Is.True);
    }

    [Test]
    public void IsConditionMet_CheckboxControllerDisabled_ReturnsFalse()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Use Secure Connection", requiredGroup: null, type: ConnectedSystemSettingType.CheckBox, checkboxValue: false),
            CreateSettingValue("Certificate Validation", requiredGroup: null, type: ConnectedSystemSettingType.DropDown,
                requiredWhenSetting: "Use Secure Connection", requiredWhenValue: "true")
        };

        // Act & Assert
        Assert.That(ConnectorSettingValidator.IsConditionMet(settingValues, settingValues[1].Setting), Is.False);
    }

    [Test]
    public void IsSettingRequired_RequiredWhenConditionMet_ReturnsTrue()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Delete Behaviour", requiredGroup: null, type: ConnectedSystemSettingType.DropDown, stringValue: "Disable"),
            CreateSettingValue("Disable Attribute", requiredGroup: null, type: ConnectedSystemSettingType.String,
                requiredWhenSetting: "Delete Behaviour", requiredWhenValue: "Disable")
        };

        // Act & Assert
        Assert.That(ConnectorSettingValidator.IsSettingRequired(settingValues, settingValues[1].Setting), Is.True);
    }

    [Test]
    public void IsSettingRequired_RequiredWhenConditionNotMet_ReturnsFalse()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            CreateSettingValue("Delete Behaviour", requiredGroup: null, type: ConnectedSystemSettingType.DropDown, stringValue: "Delete"),
            CreateSettingValue("Disable Attribute", requiredGroup: null, type: ConnectedSystemSettingType.String,
                requiredWhenSetting: "Delete Behaviour", requiredWhenValue: "Disable")
        };

        // Act & Assert
        Assert.That(ConnectorSettingValidator.IsSettingRequired(settingValues, settingValues[1].Setting), Is.False);
    }

    #endregion

    private static ConnectedSystemSettingValue CreateSettingValue(
        string name,
        string? requiredGroup,
        ConnectedSystemSettingType type = ConnectedSystemSettingType.String,
        string? stringValue = null,
        int? intValue = null,
        ConnectorSettingRequiredGroupCardinality cardinality = ConnectorSettingRequiredGroupCardinality.AtLeastOne,
        bool required = false,
        bool checkboxValue = false,
        string? requiredWhenSetting = null,
        string? requiredWhenValue = null)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting
            {
                Name = name,
                Type = type,
                RequiredGroup = requiredGroup,
                RequiredGroupCardinality = cardinality,
                Required = required,
                RequiredWhenSetting = requiredWhenSetting,
                RequiredWhenValue = requiredWhenValue
            },
            StringValue = stringValue,
            IntValue = intValue,
            CheckboxValue = checkboxValue
        };
    }
}
