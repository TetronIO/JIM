// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers bringing an existing Connected System's setting values into line with its Connector Definition. A Connected
/// System receives one value per setting when it is created, and nothing afterwards: a setting a Connector gains in a
/// later release never reached systems created before it (so it could not be seen or configured on them), and a
/// default a setting gains later never reached their unset values (so the portal showed a blank where the Connector
/// was quietly applying that default).
/// </summary>
[TestFixture]
public class ConnectedSystemSettingValueReconciliationTests
{
    [Test]
    public void ReconcileSettingValues_SettingWithNoValue_AddsItCarryingItsDefault()
    {
        var existing = StringSetting(1, "Host");
        var added = DropDownSetting(2, "Delete Behaviour", defaultValue: "Delete");
        var connectedSystem = SystemWith(new ConnectedSystemSettingValue { Setting = existing, StringValue = "dc01" });

        var changed = ConnectedSystemServer.ReconcileSettingValues(connectedSystem, [existing, added]);

        Assert.That(changed, Is.EqualTo(new[] { "Delete Behaviour" }));
        var value = connectedSystem.SettingValues.Single(v => v.Setting == added);
        Assert.That(value.StringValue, Is.EqualTo("Delete"));
        Assert.That(value.ConnectedSystem, Is.SameAs(connectedSystem));
    }

    [Test]
    public void ReconcileSettingValues_AddedIntegerAndCheckBoxSettings_CarryTheirDefaults()
    {
        var retries = new ConnectorDefinitionSetting { Id = 1, Name = "Maximum Retries", Type = ConnectedSystemSettingType.Integer, DefaultIntValue = 3 };
        var skipHidden = new ConnectorDefinitionSetting { Id = 2, Name = "Skip Hidden Partitions", Type = ConnectedSystemSettingType.CheckBox, DefaultCheckboxValue = true };
        var connectedSystem = SystemWith();

        ConnectedSystemServer.ReconcileSettingValues(connectedSystem, [retries, skipHidden]);

        Assert.That(connectedSystem.SettingValues.Single(v => v.Setting == retries).IntValue, Is.EqualTo(3));
        Assert.That(connectedSystem.SettingValues.Single(v => v.Setting == skipHidden).CheckboxValue, Is.True);
    }

    [Test]
    public void ReconcileSettingValues_AddedSettingWithNoDefault_IsAddedUnset()
    {
        var scope = StringSetting(1, "OAuth Scope");
        var connectedSystem = SystemWith();

        var changed = ConnectedSystemServer.ReconcileSettingValues(connectedSystem, [scope]);

        Assert.That(changed, Is.EqualTo(new[] { "OAuth Scope" }));
        Assert.That(connectedSystem.SettingValues.Single().StringValue, Is.Null);
    }

    [Test]
    public void ReconcileSettingValues_UnsetValueWhoseSettingDeclaresADefault_TakesTheDefault()
    {
        // Behaviour-preserving: a Connector applies its declared default when the value is unset, so recording that
        // default changes what the portal shows, not what the Connector does.
        var deleteBehaviour = DropDownSetting(1, "Delete Behaviour", defaultValue: "Delete");
        var timeout = new ConnectorDefinitionSetting { Id = 2, Name = "Search Timeout", Type = ConnectedSystemSettingType.Integer, DefaultIntValue = 300 };
        var connectedSystem = SystemWith(
            new ConnectedSystemSettingValue { Setting = deleteBehaviour, StringValue = null },
            new ConnectedSystemSettingValue { Setting = timeout, IntValue = null });

        var changed = ConnectedSystemServer.ReconcileSettingValues(connectedSystem, [deleteBehaviour, timeout]);

        Assert.That(changed, Is.EquivalentTo(new[] { "Delete Behaviour", "Search Timeout" }));
        Assert.That(connectedSystem.SettingValues.Single(v => v.Setting == deleteBehaviour).StringValue, Is.EqualTo("Delete"));
        Assert.That(connectedSystem.SettingValues.Single(v => v.Setting == timeout).IntValue, Is.EqualTo(300));
    }

    [Test]
    public void ReconcileSettingValues_ValuesTheAdministratorSet_AreLeftAlone()
    {
        // An explicit choice always stands, including an empty string: that is not the unset state the Connector's
        // fallback covers, so replacing it could change what the Connector does.
        var deleteBehaviour = DropDownSetting(1, "Delete Behaviour", defaultValue: "Delete");
        var placeholder = StringSetting(2, "Group Placeholder Member DN", defaultValue: "cn=placeholder");
        var timeout = new ConnectorDefinitionSetting { Id = 3, Name = "Search Timeout", Type = ConnectedSystemSettingType.Integer, DefaultIntValue = 300 };
        var connectedSystem = SystemWith(
            new ConnectedSystemSettingValue { Setting = deleteBehaviour, StringValue = "Disable" },
            new ConnectedSystemSettingValue { Setting = placeholder, StringValue = "" },
            new ConnectedSystemSettingValue { Setting = timeout, IntValue = 60 });

        var changed = ConnectedSystemServer.ReconcileSettingValues(connectedSystem, [deleteBehaviour, placeholder, timeout]);

        Assert.That(changed, Is.Empty);
        Assert.That(connectedSystem.SettingValues.Single(v => v.Setting == deleteBehaviour).StringValue, Is.EqualTo("Disable"));
        Assert.That(connectedSystem.SettingValues.Single(v => v.Setting == placeholder).StringValue, Is.EqualTo(""));
        Assert.That(connectedSystem.SettingValues.Single(v => v.Setting == timeout).IntValue, Is.EqualTo(60));
    }

    [Test]
    public void ReconcileSettingValues_SystemAlreadyInLine_ReportsNoChanges()
    {
        var host = StringSetting(1, "Host");
        var deleteBehaviour = DropDownSetting(2, "Delete Behaviour", defaultValue: "Delete");
        var connectedSystem = SystemWith(
            new ConnectedSystemSettingValue { Setting = host, StringValue = null },
            new ConnectedSystemSettingValue { Setting = deleteBehaviour, StringValue = "Delete" });

        var changed = ConnectedSystemServer.ReconcileSettingValues(connectedSystem, [host, deleteBehaviour]);

        Assert.That(changed, Is.Empty);
        Assert.That(connectedSystem.SettingValues, Has.Count.EqualTo(2));
    }

    [Test]
    public void ReconcileSettingValues_ValuesAreMatchedToSettingsById_NotByInstance()
    {
        // A Connected System loaded in one query and a Connector Definition loaded in another hold different instances
        // of the same setting; matching on the instance would add a second value for every setting.
        var definitionCopy = DropDownSetting(7, "Delete Behaviour", defaultValue: "Delete");
        var systemCopy = DropDownSetting(7, "Delete Behaviour", defaultValue: "Delete");
        var connectedSystem = SystemWith(new ConnectedSystemSettingValue { Setting = systemCopy, StringValue = "Disable" });

        var changed = ConnectedSystemServer.ReconcileSettingValues(connectedSystem, [definitionCopy]);

        Assert.That(changed, Is.Empty);
        Assert.That(connectedSystem.SettingValues, Has.Count.EqualTo(1));
    }

    [Test]
    public void NewSettingValue_AppliesTheSettingDefaultForItsType()
    {
        // The same rules creation has always used, now shared with reconciliation so the two cannot drift.
        Assert.That(ConnectedSystemServer.NewSettingValue(StringSetting(1, "Delimiter", defaultValue: " , ")).StringValue, Is.EqualTo(","));
        Assert.That(ConnectedSystemServer.NewSettingValue(DropDownSetting(2, "Mode", defaultValue: "Import Only")).StringValue, Is.EqualTo("Import Only"));
        Assert.That(ConnectedSystemServer.NewSettingValue(new ConnectorDefinitionSetting { Id = 3, Name = "Port", Type = ConnectedSystemSettingType.Integer, DefaultIntValue = 389 }).IntValue, Is.EqualTo(389));
        Assert.That(ConnectedSystemServer.NewSettingValue(new ConnectorDefinitionSetting { Id = 4, Name = "Use SSL", Type = ConnectedSystemSettingType.CheckBox, DefaultCheckboxValue = true }).CheckboxValue, Is.True);
        Assert.That(ConnectedSystemServer.NewSettingValue(new ConnectorDefinitionSetting { Id = 5, Name = "Password", Type = ConnectedSystemSettingType.StringEncrypted }).StringEncryptedValue, Is.Null);
    }

    private static ConnectedSystem SystemWith(params ConnectedSystemSettingValue[] values)
    {
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Yellowstone APAC" };
        foreach (var value in values)
        {
            value.ConnectedSystem = connectedSystem;
            connectedSystem.SettingValues.Add(value);
        }
        return connectedSystem;
    }

    private static ConnectorDefinitionSetting StringSetting(int id, string name, string? defaultValue = null) =>
        new() { Id = id, Name = name, Type = ConnectedSystemSettingType.String, DefaultStringValue = defaultValue };

    private static ConnectorDefinitionSetting DropDownSetting(int id, string name, string? defaultValue = null) =>
        new() { Id = id, Name = name, Type = ConnectedSystemSettingType.DropDown, DefaultStringValue = defaultValue, DropDownValues = [] };
}
