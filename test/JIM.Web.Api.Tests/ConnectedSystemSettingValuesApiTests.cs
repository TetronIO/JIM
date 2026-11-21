// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using System.Text.Json;
using JIM.Models.Staging;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// A Connected System's setting values, read through the API as the portal shows them, so a script can check a
/// configuration before changing it. An encrypted value never leaves JIM: only whether one is held.
/// </summary>
[TestFixture]
public class ConnectedSystemSettingValuesApiTests
{
    [Test]
    public void FromEntity_ReportsEachSettingsValueByItsTypeInDisplayOrder()
    {
        var entity = System(
            Value(Setting(2, "Port", ConnectedSystemSettingType.Integer, displayOrder: 2), intValue: 636),
            Value(Setting(1, "Host", ConnectedSystemSettingType.String, displayOrder: 1), stringValue: "dc1.contoso.com"),
            Value(Setting(3, "Use SSL", ConnectedSystemSettingType.CheckBox, displayOrder: 3), checkboxValue: true),
            Value(Setting(4, "Mode", ConnectedSystemSettingType.DropDown, displayOrder: 4), stringValue: "Full"));

        var values = ConnectedSystemDetailDto.FromEntity(entity).SettingValues;

        Assert.That(values.Select(v => v.Name), Is.EqualTo(new[] { "Host", "Port", "Use SSL", "Mode" }));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(values[0].SettingId, Is.EqualTo(1));
            Assert.That(values[0].Type, Is.EqualTo(ConnectedSystemSettingType.String));
            Assert.That(values[0].StringValue, Is.EqualTo("dc1.contoso.com"));
            Assert.That(values[1].IntValue, Is.EqualTo(636));
            Assert.That(values[2].CheckboxValue, Is.True);
            Assert.That(values[3].StringValue, Is.EqualTo("Full"));
            Assert.That(values.Select(v => v.HasValue), Is.All.True);
        }
    }

    [Test]
    public void FromEntity_EncryptedSetting_SaysWhetherOneIsHeldAndNeverCarriesIt()
    {
        var entity = System(
            Value(Setting(1, "Password", ConnectedSystemSettingType.StringEncrypted), encryptedValue: "$JIM$v1$ciphertext"),
            Value(Setting(2, "Client Secret", ConnectedSystemSettingType.StringEncrypted)));

        var dto = ConnectedSystemDetailDto.FromEntity(entity);

        var json = JsonSerializer.Serialize(dto);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.SettingValues[0].HasValue, Is.True);
            Assert.That(dto.SettingValues[0].StringValue, Is.Null);
            Assert.That(dto.SettingValues[1].HasValue, Is.False);
            Assert.That(json, Does.Not.Contain("ciphertext"));
            Assert.That(json, Does.Not.Contain("$JIM$"));
        }
    }

    [Test]
    public void FromEntity_SettingWithNothingSupplied_HasNoValue()
    {
        var entity = System(
            Value(Setting(1, "Host", ConnectedSystemSettingType.String), stringValue: ""),
            Value(Setting(2, "Port", ConnectedSystemSettingType.Integer)),
            Value(Setting(3, "Use SSL", ConnectedSystemSettingType.CheckBox)));

        var values = ConnectedSystemDetailDto.FromEntity(entity).SettingValues;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(values[0].HasValue, Is.False);
            Assert.That(values[1].HasValue, Is.False);
            Assert.That(values[1].IntValue, Is.Null);
            // A tick box always holds an answer.
            Assert.That(values[2].HasValue, Is.True);
            Assert.That(values[2].CheckboxValue, Is.False);
        }
    }

    [TestCase(ConnectedSystemSettingType.Heading)]
    [TestCase(ConnectedSystemSettingType.Label)]
    [TestCase(ConnectedSystemSettingType.Divider)]
    public void FromEntity_LeavesOutWhatOnlyLaysOutTheForm(ConnectedSystemSettingType type)
    {
        var entity = System(
            Value(Setting(1, "Connection", type)),
            Value(Setting(2, "Host", ConnectedSystemSettingType.String), stringValue: "dc1"));

        Assert.That(ConnectedSystemDetailDto.FromEntity(entity).SettingValues.Select(v => v.Name), Is.EqualTo(new[] { "Host" }));
    }

    [Test]
    public void FromEntity_OnlyTheValueColumnForItsType_IsReported()
    {
        // A drop-down's value is a string; a stray integer or tick left on the row by an earlier type is not reported.
        var entity = System(Value(Setting(1, "Mode", ConnectedSystemSettingType.DropDown), stringValue: "Full", intValue: 5, checkboxValue: true));

        var value = ConnectedSystemDetailDto.FromEntity(entity).SettingValues.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.StringValue, Is.EqualTo("Full"));
            Assert.That(value.IntValue, Is.Null);
            Assert.That(value.CheckboxValue, Is.Null);
        }
    }

    [Test]
    public void FromEntity_NoSettingValuesLoaded_IsAnEmptyList()
    {
        Assert.That(ConnectedSystemDetailDto.FromEntity(new ConnectedSystem { Name = "HR System" }).SettingValues, Is.Empty);
    }

    private static ConnectedSystem System(params ConnectedSystemSettingValue[] values) =>
        new() { Name = "Directory", SettingValues = values.ToList() };

    private static ConnectorDefinitionSetting Setting(int id, string name, ConnectedSystemSettingType type, int displayOrder = 0) =>
        new() { Id = id, Name = name, Type = type, DisplayOrder = displayOrder == 0 ? id : displayOrder };

    private static ConnectedSystemSettingValue Value(ConnectorDefinitionSetting setting, string? stringValue = null, string? encryptedValue = null,
        int? intValue = null, bool checkboxValue = false) =>
        new() { Setting = setting, StringValue = stringValue, StringEncryptedValue = encryptedValue, IntValue = intValue, CheckboxValue = checkboxValue };
}
