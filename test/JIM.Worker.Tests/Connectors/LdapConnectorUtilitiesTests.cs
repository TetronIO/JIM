using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class LdapConnectorUtilitiesTests
{
    #region GetLdapAttributeDataType tests

    [Test]
    public void GetLdapAttributeDataType_OmSyntax1_ReturnsBoolean()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(1);
        Assert.That(result, Is.EqualTo(AttributeDataType.Boolean));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax10_ReturnsBoolean()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(10);
        Assert.That(result, Is.EqualTo(AttributeDataType.Boolean));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax2_ReturnsNumber()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(2);
        Assert.That(result, Is.EqualTo(AttributeDataType.Number));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax65_ReturnsLongNumber()
    {
        // omSyntax 65 = Large Integer (accountExpires, pwdLastSet, etc.)
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(65);
        Assert.That(result, Is.EqualTo(AttributeDataType.LongNumber));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax3_ReturnsBinary()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(3);
        Assert.That(result, Is.EqualTo(AttributeDataType.Binary));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax4_ReturnsBinary()
    {
        // omSyntax 4 = OctetString (photo, objectSid, logonHours)
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(4);
        Assert.That(result, Is.EqualTo(AttributeDataType.Binary));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax6_ReturnsText()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(6);
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax18_ReturnsText()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(18);
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax19_ReturnsText()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(19);
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax20_ReturnsText()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(20);
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax22_ReturnsText()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(22);
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax27_ReturnsText()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(27);
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax64_ReturnsText()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(64);
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax23_ReturnsDateTime()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(23);
        Assert.That(result, Is.EqualTo(AttributeDataType.DateTime));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax24_ReturnsDateTime()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(24);
        Assert.That(result, Is.EqualTo(AttributeDataType.DateTime));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax127_ReturnsReference()
    {
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(127);
        Assert.That(result, Is.EqualTo(AttributeDataType.Reference));
    }

    [Test]
    public void GetLdapAttributeDataType_UnsupportedOmSyntax_ThrowsInvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() => LdapConnectorUtilities.GetLdapAttributeDataType(999));
    }

    [Test]
    public void GetLdapAttributeDataType_OmSyntax0_ThrowsInvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() => LdapConnectorUtilities.GetLdapAttributeDataType(0));
    }

    [Test]
    public void GetLdapAttributeDataType_NegativeOmSyntax_ThrowsInvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() => LdapConnectorUtilities.GetLdapAttributeDataType(-1));
    }

    #endregion

    #region GetPaginationTokenName tests

    [Test]
    public void GetPaginationTokenName_ValidInputs_ReturnsExpectedFormat()
    {
        var container = new ConnectedSystemContainer
        {
            ExternalId = "OU=Users,DC=corp,DC=local"
        };
        var objectType = new ConnectedSystemObjectType
        {
            Id = 42
        };

        var result = LdapConnectorUtilities.GetPaginationTokenName(container, objectType);

        Assert.That(result, Is.EqualTo("OU=Users,DC=corp,DC=local|42"));
    }

    [Test]
    public void GetPaginationTokenName_EmptyExternalId_ReturnsTokenWithEmptyPrefix()
    {
        var container = new ConnectedSystemContainer
        {
            ExternalId = ""
        };
        var objectType = new ConnectedSystemObjectType
        {
            Id = 123
        };

        var result = LdapConnectorUtilities.GetPaginationTokenName(container, objectType);

        Assert.That(result, Is.EqualTo("|123"));
    }

    [Test]
    public void GetPaginationTokenName_ContainsPipeInExternalId_ReturnsTokenWithPipe()
    {
        // Edge case: what if the DN contains a pipe? It shouldn't normally, but let's verify behaviour
        var container = new ConnectedSystemContainer
        {
            ExternalId = "OU=Test|Pipe,DC=corp,DC=local"
        };
        var objectType = new ConnectedSystemObjectType
        {
            Id = 999
        };

        var result = LdapConnectorUtilities.GetPaginationTokenName(container, objectType);

        Assert.That(result, Does.Contain("|"));
        Assert.That(result, Does.StartWith("OU=Test|Pipe,DC=corp,DC=local|"));
    }

    #endregion

    #region ParseDistinguishedName tests

    [Test]
    public void ParseDistinguishedName_StandardDn_ReturnsRdnAndParent()
    {
        var (rdn, parentDn) = LdapConnectorUtilities.ParseDistinguishedName("CN=John Smith,OU=Users,DC=example,DC=com");

        Assert.That(rdn, Is.EqualTo("CN=John Smith"));
        Assert.That(parentDn, Is.EqualTo("OU=Users,DC=example,DC=com"));
    }

    [Test]
    public void ParseDistinguishedName_SingleComponent_ReturnsRdnWithNullParent()
    {
        var (rdn, parentDn) = LdapConnectorUtilities.ParseDistinguishedName("DC=local");

        Assert.That(rdn, Is.EqualTo("DC=local"));
        Assert.That(parentDn, Is.Null);
    }

    [Test]
    public void ParseDistinguishedName_EmptyString_ReturnsNulls()
    {
        var (rdn, parentDn) = LdapConnectorUtilities.ParseDistinguishedName("");

        Assert.That(rdn, Is.Null);
        Assert.That(parentDn, Is.Null);
    }

    [Test]
    public void ParseDistinguishedName_NullInput_ReturnsNulls()
    {
        var (rdn, parentDn) = LdapConnectorUtilities.ParseDistinguishedName(null!);

        Assert.That(rdn, Is.Null);
        Assert.That(parentDn, Is.Null);
    }

    [Test]
    public void ParseDistinguishedName_EscapedCommaInRdn_ReturnsCorrectComponents()
    {
        // DN with escaped comma in the CN value: "Smith\, John"
        var (rdn, parentDn) = LdapConnectorUtilities.ParseDistinguishedName(@"CN=Smith\, John,OU=Users,DC=example,DC=com");

        Assert.That(rdn, Is.EqualTo(@"CN=Smith\, John"));
        Assert.That(parentDn, Is.EqualTo("OU=Users,DC=example,DC=com"));
    }

    [Test]
    public void ParseDistinguishedName_TwoComponents_ReturnsCorrectComponents()
    {
        var (rdn, parentDn) = LdapConnectorUtilities.ParseDistinguishedName("CN=Test User,CN=Users");

        Assert.That(rdn, Is.EqualTo("CN=Test User"));
        Assert.That(parentDn, Is.EqualTo("CN=Users"));
    }

    [Test]
    public void ParseDistinguishedName_ComplexDn_ReturnsCorrectComponents()
    {
        var (rdn, parentDn) = LdapConnectorUtilities.ParseDistinguishedName("CN=Test Joiner,CN=Users,DC=testdomain,DC=local");

        Assert.That(rdn, Is.EqualTo("CN=Test Joiner"));
        Assert.That(parentDn, Is.EqualTo("CN=Users,DC=testdomain,DC=local"));
    }

    #endregion

    #region FindUnescapedComma tests

    [Test]
    public void FindUnescapedComma_StandardDn_ReturnsFirstCommaIndex()
    {
        var result = LdapConnectorUtilities.FindUnescapedComma("CN=John,OU=Users,DC=local");

        Assert.That(result, Is.EqualTo(7)); // Index of comma after "CN=John"
    }

    [Test]
    public void FindUnescapedComma_NoComma_ReturnsMinusOne()
    {
        var result = LdapConnectorUtilities.FindUnescapedComma("DC=local");

        Assert.That(result, Is.EqualTo(-1));
    }

    [Test]
    public void FindUnescapedComma_EscapedComma_SkipsEscapedAndFindUnescaped()
    {
        // "CN=Smith\, John,OU=Users" - escaped comma at index 8, unescaped at index 16
        var result = LdapConnectorUtilities.FindUnescapedComma(@"CN=Smith\, John,OU=Users");

        Assert.That(result, Is.EqualTo(15)); // Index of unescaped comma after "John"
    }

    [Test]
    public void FindUnescapedComma_OnlyEscapedCommas_ReturnsMinusOne()
    {
        var result = LdapConnectorUtilities.FindUnescapedComma(@"CN=Smith\, John\, Jr");

        Assert.That(result, Is.EqualTo(-1));
    }

    [Test]
    public void FindUnescapedComma_EmptyString_ReturnsMinusOne()
    {
        var result = LdapConnectorUtilities.FindUnescapedComma("");

        Assert.That(result, Is.EqualTo(-1));
    }

    [Test]
    public void FindUnescapedComma_CommaAtStart_ReturnsZero()
    {
        var result = LdapConnectorUtilities.FindUnescapedComma(",CN=Test");

        Assert.That(result, Is.EqualTo(0));
    }

    #endregion
}
