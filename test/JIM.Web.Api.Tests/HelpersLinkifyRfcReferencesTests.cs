// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class HelpersLinkifyRfcReferencesTests
{
    [Test]
    public void LinkifyRfcReferences_NullInput_ReturnsEmptyString()
    {
        var result = Helpers.LinkifyRfcReferences(null);
        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void LinkifyRfcReferences_EmptyInput_ReturnsEmptyString()
    {
        var result = Helpers.LinkifyRfcReferences(string.Empty);
        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void LinkifyRfcReferences_NoRfcReference_ReturnsEncodedTextUnchanged()
    {
        var result = Helpers.LinkifyRfcReferences("A plain description with no reference.");
        Assert.That(result, Is.EqualTo("A plain description with no reference."));
    }

    [Test]
    public void LinkifyRfcReferences_NoSpaceForm_LinksToLowercaseRfcUrlAndKeepsOriginalText()
    {
        var result = Helpers.LinkifyRfcReferences("RFC2256: business category");
        Assert.That(result, Is.EqualTo(
            "<a class=\"mud-link mud-primary-text\" href=\"https://datatracker.ietf.org/doc/html/rfc2256\" target=\"_blank\" rel=\"noopener noreferrer\">RFC2256</a>: business category"));
    }

    [Test]
    public void LinkifyRfcReferences_SpacedForm_IsMatchedAndTextPreserved()
    {
        var result = Helpers.LinkifyRfcReferences("Defined by RFC 4519.");
        Assert.That(result, Does.Contain("href=\"https://datatracker.ietf.org/doc/html/rfc4519\""));
        Assert.That(result, Does.Contain(">RFC 4519</a>"));
    }

    [Test]
    public void LinkifyRfcReferences_LowercaseInput_IsMatchedAndOriginalCasingPreserved()
    {
        var result = Helpers.LinkifyRfcReferences("see rfc2798");
        Assert.That(result, Does.Contain("href=\"https://datatracker.ietf.org/doc/html/rfc2798\""));
        Assert.That(result, Does.Contain(">rfc2798</a>"));
    }

    [Test]
    public void LinkifyRfcReferences_MultipleReferences_AreAllLinked()
    {
        var result = Helpers.LinkifyRfcReferences("Originally RFC2256, updated by RFC4519.");
        Assert.That(result, Does.Contain("rfc2256"));
        Assert.That(result, Does.Contain("rfc4519"));
        Assert.That(result, Does.Contain(">RFC2256</a>"));
        Assert.That(result, Does.Contain(">RFC4519</a>"));
    }

    [Test]
    public void LinkifyRfcReferences_HtmlInSource_IsEncoded()
    {
        var result = Helpers.LinkifyRfcReferences("<script>alert('x')</script> RFC2256");
        Assert.That(result, Does.Not.Contain("<script>"));
        Assert.That(result, Does.Contain("&lt;script&gt;"));
        // The genuine RFC anchor is still emitted.
        Assert.That(result, Does.Contain("href=\"https://datatracker.ietf.org/doc/html/rfc2256\""));
    }

    [Test]
    public void LinkifyRfcReferences_TokenNotAWholeWord_IsNotLinked()
    {
        var result = Helpers.LinkifyRfcReferences("PREFIXRFC2256SUFFIX");
        Assert.That(result, Does.Not.Contain("<a "));
        Assert.That(result, Is.EqualTo("PREFIXRFC2256SUFFIX"));
    }
}
