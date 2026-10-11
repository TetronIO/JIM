// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for <see cref="DocsSite"/>: turning a docs page as the portal names it (its Markdown path under
/// <c>docs/</c>, which the COV-5 coverage check reads straight from source) into the address the published site
/// serves it from. MkDocs serves each page as a directory, and an <c>index.md</c> as its folder.
/// </summary>
[TestFixture]
public class DocsSiteTests
{
    [TestCase("configuration/connected-systems.md", "https://docs.junctional.io/configuration/connected-systems/")]
    [TestCase("configuration/connected-systems.md#the-connector-space", "https://docs.junctional.io/configuration/connected-systems/#the-connector-space")]
    [TestCase("administration/sso-setup/keycloak.md", "https://docs.junctional.io/administration/sso-setup/keycloak/")]
    [TestCase("configuration/index.md", "https://docs.junctional.io/configuration/")]
    [TestCase("configuration/index.md#start-here", "https://docs.junctional.io/configuration/#start-here")]
    [TestCase("index.md", "https://docs.junctional.io/")]
    public void PageUrl_MarkdownPage_ReturnsTheAddressThePublishedSiteServesItFrom(string docsPage, string expected)
    {
        Assert.That(DocsSite.PageUrl(docsPage), Is.EqualTo(expected));
    }

    [TestCase("configuration/connected-systems")]
    [TestCase("configuration/connected-systems/")]
    [TestCase("https://docs.junctional.io/configuration/connected-systems/")]
    [TestCase("")]
    public void PageUrl_NotAMarkdownPage_Throws(string docsPage)
    {
        Assert.That(() => DocsSite.PageUrl(docsPage), Throws.ArgumentException);
    }

    [Test]
    public void GlossaryUrl_Anchor_ReturnsThatEntryOnTheGlossaryPage()
    {
        Assert.That(DocsSite.GlossaryUrl("connector-space"), Is.EqualTo("https://docs.junctional.io/reference/glossary/#connector-space"));
    }
}
