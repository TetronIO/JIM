// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Localisation;
using JIM.Web.Localisation;
using NUnit.Framework;

namespace JIM.Web.Tests.Localisation;

/// <summary>
/// A stored record keeps its English beside its message, so when the message cannot be rendered (its Connector was
/// uninstalled, a key was renamed in an upgrade, a translation is malformed) the reader sees that English, never a key
/// (#3103).
/// </summary>
[TestFixture]
public class MessageDisplayTests
{
    private const string StoredEnglish = "Grant User.ReadWrite to the app registration.";

    [Test]
    public void Show_MessageThatRenders_ShowsItInTheReadersLanguage()
    {
        using var _ = new UiCulture("fr");
        var message = ActivityMessage.Create(nameof(ActivityMessages.TargetFactoryReset));

        Assert.That(MessageDisplay.Show(message, "Factory reset"), Is.Not.EqualTo("Factory reset").And.Not.EqualTo("TargetFactoryReset"));
    }

    [Test]
    public void Show_CatalogueNoLongerKnown_ShowsTheStoredEnglish()
    {
        using var _ = new UiCulture("fr");
        var message = JimMessage.Create("connector:uninstalled.example/Messages", "PermissionMissing", "User.ReadWrite");

        Assert.That(MessageDisplay.Show(message, StoredEnglish), Is.EqualTo(StoredEnglish));
    }

    [Test]
    public void Show_KeyNoLongerInTheCatalogue_ShowsTheStoredEnglish()
    {
        using var _ = new UiCulture("fr");
        var message = JimMessage.Create(ApplicationMessageCatalogues.Activities, "RenamedInALaterVersion", "User.ReadWrite");

        Assert.That(MessageDisplay.Show(message, StoredEnglish), Is.EqualTo(StoredEnglish));
    }

    private sealed class UiCulture : System.IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

        public UiCulture(string name) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentUICulture = _previous;
    }
}
