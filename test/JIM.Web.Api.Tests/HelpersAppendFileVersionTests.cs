// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.IO;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The theme stylesheets are the one set of assets the portal serves without a version, because they are not
/// emitted by a tag helper: the path is held on <c>ThemeSettings</c> and used both by the <c>&lt;link&gt;</c> in
/// <c>_Layout.cshtml</c> and by the runtime theme swap in <c>MainLayout</c>, so <c>asp-append-version</c> would
/// only cover half of it. A browser therefore kept a theme file across an upgrade that changed it, while every
/// other stylesheet refreshed, and the stale colours looked exactly like a fix that had not worked.
/// </summary>
[TestFixture]
public class HelpersAppendFileVersionTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), "jim-file-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(_root, "css", "themes"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void AppendFileVersion_ForAFileThatExists_AppendsAVersionQuery()
    {
        WriteTheme("navy-o6-dark.css", ":root { --mud-palette-primary: #764ce8; }");

        var result = Helpers.AppendFileVersion(_root, "css/themes/navy-o6-dark.css");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Does.StartWith("css/themes/navy-o6-dark.css?v="));
            Assert.That(result, Has.Length.GreaterThan("css/themes/navy-o6-dark.css?v=".Length),
                "the version must actually carry a value");
        }
    }

    [Test]
    public void AppendFileVersion_WhenTheContentChanges_ChangesTheVersion()
    {
        WriteTheme("navy-o6-dark.css", ":root { --mud-palette-primary: #764ce8; }");
        var before = Helpers.AppendFileVersion(_root, "css/themes/navy-o6-dark.css");

        WriteTheme("navy-o6-dark.css", ":root { --mud-palette-primary: #b499ff; }");
        var after = Helpers.AppendFileVersion(_root, "css/themes/navy-o6-dark.css");

        Assert.That(after, Is.Not.EqualTo(before),
            "an upgrade that changes a theme must change its URL, or the browser keeps the old one");
    }

    [Test]
    public void AppendFileVersion_ForUnchangedContent_IsStable()
    {
        WriteTheme("navy-o6-dark.css", ":root { --mud-palette-primary: #764ce8; }");

        var first = Helpers.AppendFileVersion(_root, "css/themes/navy-o6-dark.css");
        var second = Helpers.AppendFileVersion(_root, "css/themes/navy-o6-dark.css");

        Assert.That(second, Is.EqualTo(first),
            "the version is derived from content, so it must not change on its own between restarts");
    }

    /// <summary>
    /// A theme file that is not where it is expected must not stop the portal starting; the colours are worth
    /// less than the application. The path is returned unversioned, exactly as it behaved before.
    /// </summary>
    [Test]
    public void AppendFileVersion_ForAMissingFile_ReturnsThePathUnchanged()
    {
        var result = Helpers.AppendFileVersion(_root, "css/themes/does-not-exist.css");

        Assert.That(result, Is.EqualTo("css/themes/does-not-exist.css"));
    }

    [Test]
    public void AppendFileVersion_WithNoWebRoot_ReturnsThePathUnchanged()
    {
        var result = Helpers.AppendFileVersion(null, "css/themes/navy-o6-dark.css");

        Assert.That(result, Is.EqualTo("css/themes/navy-o6-dark.css"));
    }

    /// <summary>
    /// The version travels into a URL, so it may only contain characters that survive one unescaped.
    /// </summary>
    [Test]
    public void AppendFileVersion_ProducesAUrlSafeVersion()
    {
        WriteTheme("navy-o6-dark.css", ":root { --mud-palette-primary: #764ce8; }");

        var version = Helpers.AppendFileVersion(_root, "css/themes/navy-o6-dark.css").Split("?v=")[1];

        Assert.That(version, Does.Match("^[A-Za-z0-9_-]+$"),
            "base64url only: '+', '/' and '=' would need escaping in a query string");
    }

    private void WriteTheme(string fileName, string content) =>
        File.WriteAllText(Path.Join(_root, "css", "themes", fileName), content);
}
