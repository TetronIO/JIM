// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class LdapDistinguishedNameTests
{
    #region Parse / TryParse structure

    [Test]
    public void Parse_StandardDn_SplitsIntoRdnsLeafFirst()
    {
        var dn = LdapDistinguishedName.Parse("CN=John Smith,OU=Users,DC=example,DC=com");

        Assert.That(dn.Rdns, Has.Count.EqualTo(4));
        Assert.That(dn.LeafRdn.Source, Is.EqualTo("CN=John Smith"));
        Assert.That(dn.LeafRdn.Components[0].Type, Is.EqualTo("CN"));
        Assert.That(dn.LeafRdn.Components[0].Value, Is.EqualTo("John Smith"));
    }

    [Test]
    public void Parse_StandardDn_ParentIsVerbatimTail()
    {
        var dn = LdapDistinguishedName.Parse("CN=John Smith,OU=Users,DC=example,DC=com");

        Assert.That(dn.Parent, Is.Not.Null);
        Assert.That(dn.Parent!.ToString(), Is.EqualTo("OU=Users,DC=example,DC=com"));
    }

    [Test]
    public void Parse_SingleRdn_HasNoParent()
    {
        var dn = LdapDistinguishedName.Parse("DC=local");

        Assert.That(dn.Rdns, Has.Count.EqualTo(1));
        Assert.That(dn.Parent, Is.Null);
    }

    [Test]
    public void ToString_RoundTripsOriginalText()
    {
        const string source = "CN=John Smith,OU=Users,DC=example,DC=com";
        Assert.That(LdapDistinguishedName.Parse(source).ToString(), Is.EqualTo(source));
    }

    #endregion

    #region Escaping

    [Test]
    public void Parse_EscapedCommaInValue_DoesNotSplitAndUnescapesValue()
    {
        var dn = LdapDistinguishedName.Parse(@"CN=Smith\, John,OU=Users,DC=example,DC=com");

        Assert.That(dn.LeafRdn.Source, Is.EqualTo(@"CN=Smith\, John"));
        Assert.That(dn.LeafRdn.Components[0].Value, Is.EqualTo("Smith, John"));
        Assert.That(dn.Parent!.ToString(), Is.EqualTo("OU=Users,DC=example,DC=com"));
    }

    [Test]
    public void Parse_HexPairEscape_ResolvesToCharacter()
    {
        // \2C is the hex-pair escape for a comma (RFC 4514 s2.4).
        var dn = LdapDistinguishedName.Parse(@"CN=Smith\2C John,DC=example,DC=com");

        Assert.That(dn.LeafRdn.Components[0].Value, Is.EqualTo("Smith, John"));
    }

    [Test]
    public void Parse_EscapedBackslashThenComma_TreatsCommaAsSeparator()
    {
        // Two backslashes are an escaped backslash (even count), so the comma after them is a real separator.
        var dn = LdapDistinguishedName.Parse(@"CN=foo\\,OU=Bar,DC=example,DC=com");

        Assert.That(dn.LeafRdn.Source, Is.EqualTo(@"CN=foo\\"));
        Assert.That(dn.LeafRdn.Components[0].Value, Is.EqualTo(@"foo\"));
        Assert.That(dn.Parent!.ToString(), Is.EqualTo("OU=Bar,DC=example,DC=com"));
    }

    [Test]
    public void Parse_QuotedValueWithComma_DoesNotSplit()
    {
        // RFC 2253 quoting: the comma inside the quotes is part of the value, not a separator.
        var dn = LdapDistinguishedName.Parse("CN=\"Smith, John\",DC=example,DC=com");

        Assert.That(dn.Rdns, Has.Count.EqualTo(3));
        Assert.That(dn.LeafRdn.Components[0].Value, Is.EqualTo("Smith, John"));
        Assert.That(dn.Parent!.ToString(), Is.EqualTo("DC=example,DC=com"));
    }

    #endregion

    #region Multi-valued RDNs

    [Test]
    public void Parse_MultiValuedRdn_SplitsIntoComponents()
    {
        var dn = LdapDistinguishedName.Parse("CN=John+SN=Smith,DC=example,DC=com");

        Assert.That(dn.LeafRdn.Components, Has.Count.EqualTo(2));
        Assert.That(dn.LeafRdn.Components[0].Type, Is.EqualTo("CN"));
        Assert.That(dn.LeafRdn.Components[0].Value, Is.EqualTo("John"));
        Assert.That(dn.LeafRdn.Components[1].Type, Is.EqualTo("SN"));
        Assert.That(dn.LeafRdn.Components[1].Value, Is.EqualTo("Smith"));
    }

    [Test]
    public void Parse_EscapedPlusInValue_DoesNotSplitComponents()
    {
        var dn = LdapDistinguishedName.Parse(@"CN=A\+B,DC=example,DC=com");

        Assert.That(dn.LeafRdn.Components, Has.Count.EqualTo(1));
        Assert.That(dn.LeafRdn.Components[0].Value, Is.EqualTo("A+B"));
    }

    #endregion

    #region Empty values and type trimming

    [Test]
    public void Parse_EmptyComponentValue_ParsesWithEmptyValue()
    {
        var dn = LdapDistinguishedName.Parse("CN=,DC=example,DC=com");

        Assert.That(dn.LeafRdn.Components[0].Type, Is.EqualTo("CN"));
        Assert.That(dn.LeafRdn.Components[0].Value, Is.EqualTo(string.Empty));
    }

    [Test]
    public void Parse_WhitespaceAroundType_TrimsType()
    {
        var dn = LdapDistinguishedName.Parse("CN = John,DC=example,DC=com");

        Assert.That(dn.LeafRdn.Components[0].Type, Is.EqualTo("CN"));
    }

    #endregion

    #region Malformed input

    [Test]
    public void TryParse_Null_ReturnsFalse()
    {
        Assert.That(LdapDistinguishedName.TryParse(null, out var result), Is.False);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void TryParse_Empty_ReturnsFalse()
    {
        Assert.That(LdapDistinguishedName.TryParse("", out _), Is.False);
    }

    [Test]
    public void TryParse_NoEqualsSign_ReturnsFalse()
    {
        Assert.That(LdapDistinguishedName.TryParse("foo", out _), Is.False);
    }

    [Test]
    public void TryParse_EmptyRdnFromLeadingComma_ReturnsFalse()
    {
        Assert.That(LdapDistinguishedName.TryParse(",CN=Test", out _), Is.False);
    }

    [Test]
    public void Parse_Malformed_ThrowsFormatException()
    {
        Assert.That(() => LdapDistinguishedName.Parse("foo"), Throws.TypeOf<FormatException>());
    }

    #endregion

    #region SplitTopLevel helper

    [Test]
    public void SplitTopLevel_EscapedSeparator_IsNotSplit()
    {
        var parts = LdapDistinguishedName.SplitTopLevel(@"CN=Smith\, John,OU=Users", ',');

        Assert.That(parts, Has.Count.EqualTo(2));
        Assert.That(parts[0], Is.EqualTo(@"CN=Smith\, John"));
        Assert.That(parts[1], Is.EqualTo("OU=Users"));
    }

    [Test]
    public void SplitTopLevel_NoSeparator_ReturnsWholeString()
    {
        var parts = LdapDistinguishedName.SplitTopLevel("DC=local", ',');

        Assert.That(parts, Has.Count.EqualTo(1));
        Assert.That(parts[0], Is.EqualTo("DC=local"));
    }

    #endregion
}
