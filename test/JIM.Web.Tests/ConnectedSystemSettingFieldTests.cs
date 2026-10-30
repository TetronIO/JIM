// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Threading.Tasks;
using Bunit;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers how a single Connected System setting is rendered for each setting type.
/// </summary>
/// <remarks>
/// Guards the defect where a <see cref="ConnectedSystemSettingType.Text"/> setting (the SQL Connector's
/// required Object Types document) rendered as "Oops. Unknown setting type!", leaving no way to configure
/// that Connector from the portal at all.
/// </remarks>
[TestFixture]
public class ConnectedSystemSettingFieldTests : JimComponentTestContext
{
    private static ConnectedSystemSettingValue TextSettingValue(string? value = null, string? example = null)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting
            {
                Id = 1,
                Name = "Object Types",
                Description = "Which object types this database holds.",
                Example = example,
                Required = true,
                Type = ConnectedSystemSettingType.Text,
                Category = ConnectedSystemSettingCategory.Schema
            },
            StringValue = value
        };
    }

    private IRenderedComponent<ConnectedSystemSettingField> RenderField(ConnectedSystemSettingValue settingValue)
    {
        return Render<ConnectedSystemSettingField>(p => p
            .Add(c => c.SettingValue, settingValue)
            .Add(c => c.IsRequired, settingValue.Setting.Required));
    }

    [Test]
    public void Render_TextSetting_RendersAnEditableMultiLineField()
    {
        var cut = RenderField(TextSettingValue());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Not.Contain("Unknown setting type"));
            Assert.That(cut.FindAll("textarea"), Has.Count.EqualTo(1), "A Text setting holds a multi-line document, so it needs a text area.");
        }
    }

    [Test]
    public void Render_TextSettingWithValue_ShowsTheSavedValue()
    {
        const string document = "{ \"objectTypes\": [] }";

        var cut = RenderField(TextSettingValue(document));

        Assert.That(cut.Find("textarea").GetAttribute("value") ?? cut.Find("textarea").TextContent, Is.EqualTo(document));
    }

    [Test]
    public async Task Change_TextSetting_WritesTheTypedDocumentToTheStringValueAsync()
    {
        var settingValue = TextSettingValue();
        const string document = "{\n  \"objectTypes\": []\n}";

        var cut = RenderField(settingValue);
        await cut.Find("textarea").ChangeAsync(document);

        Assert.That(settingValue.StringValue, Is.EqualTo(document));
    }

    [Test]
    public void Render_SettingWithExample_ShowsTheExample()
    {
        const string example = "{\n  \"objectTypes\": [ { \"name\": \"Person\" } ]\n}";

        var cut = RenderField(TextSettingValue(example: example));

        Assert.That(cut.Find("[data-testid=jim-setting-example]").TextContent, Does.Contain("\"name\": \"Person\""));
    }

    [Test]
    public void Render_SettingWithoutExample_ShowsNoExample()
    {
        var cut = RenderField(TextSettingValue());

        Assert.That(cut.FindAll("[data-testid=jim-setting-example]"), Is.Empty);
    }

    [Test]
    public void Render_DropDownSettingWithExample_ShowsTheExample()
    {
        var settingValue = new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting
            {
                Id = 2,
                Name = "Delta Import Mode",
                Description = "How a Delta Import finds out what has changed.",
                Example = "{ \"changeLog\": {} }",
                Type = ConnectedSystemSettingType.DropDown,
                DropDownValues = ["Change-Log Table", "Watermark Column"],
                Category = ConnectedSystemSettingCategory.Schema
            }
        };

        var cut = RenderField(settingValue);

        Assert.That(cut.Find("[data-testid=jim-setting-example]").TextContent, Does.Contain("changeLog"));
    }
}
