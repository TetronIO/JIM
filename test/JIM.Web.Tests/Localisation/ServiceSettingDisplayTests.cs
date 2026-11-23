// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Web.Localisation;
using JIM.Web.Resources;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// Service Setting names and descriptions shown in the portal come from <c>ServiceSettingText.resx</c>, looked up by the
/// setting's key (#3030). Every setting an administrator can see must have both, or it would read in English in French.
/// </summary>
[TestFixture]
public class ServiceSettingDisplayTests
{
    // Internal bookkeeping for the configuration-change hash; never shown to an administrator.
    private static readonly string[] NotShownToAdministrators = [Constants.SettingKeys.ConfigurationChangeHashKey];

    [Test]
    public void EveryShownServiceSetting_HasANameAndADescription()
    {
        var missing = typeof(Constants.SettingKeys)
            .GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(key => !NotShownToAdministrators.Contains(key))
            .SelectMany(key => new[]
            {
                ServiceSettingText.ResourceManager.GetString(ServiceSettingDisplay.ResourceKeyFor(key), CultureInfo.InvariantCulture) == null ? $"{key} (name)" : null,
                ServiceSettingText.ResourceManager.GetString(ServiceSettingDisplay.ResourceKeyFor(key) + "_Description", CultureInfo.InvariantCulture) == null ? $"{key} (description)" : null
            })
            .OfType<string>()
            .ToList();

        Assert.That(missing, Is.Empty, "ServiceSettingText.resx has no text for these settings");
    }

    [Test]
    [SetUICulture("fr")]
    public void Name_FrenchViewer_ReturnsTheFrenchName()
    {
        Assert.That(ServiceSettingDisplay.Name(Constants.SettingKeys.SyncPageSize, "Sync page size"), Is.EqualTo("Taille de page de synchronisation"));
    }

    [Test]
    [SetUICulture("fr")]
    public void Description_FrenchViewer_ReturnsTheFrenchDescription()
    {
        Assert.That(ServiceSettingDisplay.Description(Constants.SettingKeys.ServiceName, "English"), Does.StartWith("Nom convivial"));
    }

    [Test]
    [SetUICulture("fr")]
    public void Name_SettingWithoutText_FallsBackToTheStoredEnglish()
    {
        Assert.That(ServiceSettingDisplay.Name("Custom.Unknown", "Stored name"), Is.EqualTo("Stored name"));
    }
}
