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
    public void GetLdapAttributeDataType_OmSyntax66_ReturnsBinary()
    {
        // omSyntax 66 = Object(Replica-Link) (nTSecurityDescriptor, msExchMailboxSecurityDescriptor)
        var result = LdapConnectorUtilities.GetLdapAttributeDataType(66);
        Assert.That(result, Is.EqualTo(AttributeDataType.Binary));
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

    #region ShouldOverridePluralityToSingleValued tests

    [Test]
    public void ShouldOverridePluralityToSingleValued_DescriptionOnUserInAd_ReturnsTrue()
    {
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued("description", "user", isActiveDirectory: true);
        Assert.That(result, Is.True);
    }

    [Test]
    public void ShouldOverridePluralityToSingleValued_DescriptionOnGroupInAd_ReturnsTrue()
    {
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued("description", "group", isActiveDirectory: true);
        Assert.That(result, Is.True);
    }

    [Test]
    public void ShouldOverridePluralityToSingleValued_DescriptionOnComputerInAd_ReturnsTrue()
    {
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued("description", "computer", isActiveDirectory: true);
        Assert.That(result, Is.True);
    }

    [Test]
    public void ShouldOverridePluralityToSingleValued_DescriptionOnInetOrgPersonInAd_ReturnsTrue()
    {
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued("description", "inetOrgPerson", isActiveDirectory: true);
        Assert.That(result, Is.True);
    }

    [Test]
    public void ShouldOverridePluralityToSingleValued_DescriptionOnSamDomainInAd_ReturnsTrue()
    {
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued("description", "samDomain", isActiveDirectory: true);
        Assert.That(result, Is.True);
    }

    [Test]
    public void ShouldOverridePluralityToSingleValued_DescriptionOnSamServerInAd_ReturnsTrue()
    {
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued("description", "samServer", isActiveDirectory: true);
        Assert.That(result, Is.True);
    }

    [Test]
    public void ShouldOverridePluralityToSingleValued_DescriptionOnUserInGenericLdap_ReturnsFalse()
    {
        // Generic LDAP directories (OpenLDAP, 389DS) have no SAM layer — description is genuinely multi-valued
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued("description", "user", isActiveDirectory: false);
        Assert.That(result, Is.False);
    }

    [Test]
    public void ShouldOverridePluralityToSingleValued_DescriptionOnGroupInGenericLdap_ReturnsFalse()
    {
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued("description", "group", isActiveDirectory: false);
        Assert.That(result, Is.False);
    }

    [Test]
    public void ShouldOverridePluralityToSingleValued_DescriptionOnNonSamClassInAd_ReturnsFalse()
    {
        // Non-SAM-managed classes (e.g., organizationalUnit) should not have the override applied
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued("description", "organizationalUnit", isActiveDirectory: true);
        Assert.That(result, Is.False);
    }

    [Test]
    public void ShouldOverridePluralityToSingleValued_OtherAttributeOnUserInAd_ReturnsFalse()
    {
        // Non-SAM-enforced attributes should not be overridden even on SAM-managed classes
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued("member", "user", isActiveDirectory: true);
        Assert.That(result, Is.False);
    }

    [Test]
    public void ShouldOverridePluralityToSingleValued_CaseInsensitiveAttributeName_ReturnsTrue()
    {
        // LDAP attribute names are case-insensitive
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued("Description", "group", isActiveDirectory: true);
        Assert.That(result, Is.True);
    }

    [Test]
    public void ShouldOverridePluralityToSingleValued_CaseInsensitiveObjectClass_ReturnsTrue()
    {
        // LDAP object class names are case-insensitive
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued("description", "Group", isActiveDirectory: true);
        Assert.That(result, Is.True);
    }

    [Test]
    public void ShouldOverridePluralityToSingleValued_UpperCaseAttributeAndClass_ReturnsTrue()
    {
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued("DESCRIPTION", "USER", isActiveDirectory: true);
        Assert.That(result, Is.True);
    }

    #endregion

    #region SAM constants validation tests

    [Test]
    public void SamEnforcedSingleValuedAttributes_ContainsDescription()
    {
        Assert.That(LdapConnectorConstants.SAM_ENFORCED_SINGLE_VALUED_ATTRIBUTES, Does.Contain("description"));
    }

    [Test]
    public void SamManagedObjectClasses_ContainsAllExpectedClasses()
    {
        var expected = new[] { "user", "computer", "inetOrgPerson", "group", "samDomain", "samServer" };
        foreach (var className in expected)
        {
            Assert.That(LdapConnectorConstants.SAM_MANAGED_OBJECT_CLASSES, Does.Contain(className),
                $"Expected SAM_MANAGED_OBJECT_CLASSES to contain '{className}'");
        }
    }

    [Test]
    public void SamManagedObjectClasses_IsCaseInsensitive()
    {
        Assert.That(LdapConnectorConstants.SAM_MANAGED_OBJECT_CLASSES.Contains("USER"), Is.True);
        Assert.That(LdapConnectorConstants.SAM_MANAGED_OBJECT_CLASSES.Contains("Group"), Is.True);
        Assert.That(LdapConnectorConstants.SAM_MANAGED_OBJECT_CLASSES.Contains("COMPUTER"), Is.True);
    }

    [Test]
    public void SamEnforcedSingleValuedAttributes_IsCaseInsensitive()
    {
        Assert.That(LdapConnectorConstants.SAM_ENFORCED_SINGLE_VALUED_ATTRIBUTES.Contains("DESCRIPTION"), Is.True);
        Assert.That(LdapConnectorConstants.SAM_ENFORCED_SINGLE_VALUED_ATTRIBUTES.Contains("Description"), Is.True);
    }

    #endregion

    #region DetermineAttributeWritability tests

    [Test]
    public void DetermineAttributeWritability_SystemOnlyTrue_ReturnsReadOnly()
    {
        var result = LdapConnectorUtilities.DetermineAttributeWritability(true, null, null);
        Assert.That(result, Is.EqualTo(AttributeWritability.ReadOnly));
    }

    [Test]
    public void DetermineAttributeWritability_SystemOnlyFalse_ReturnsWritable()
    {
        var result = LdapConnectorUtilities.DetermineAttributeWritability(false, null, null);
        Assert.That(result, Is.EqualTo(AttributeWritability.Writable));
    }

    [Test]
    public void DetermineAttributeWritability_ConstructedFlag_ReturnsReadOnly()
    {
        // systemFlags = 0x4 (FLAG_ATTR_IS_CONSTRUCTED)
        var result = LdapConnectorUtilities.DetermineAttributeWritability(false, 0x4, null);
        Assert.That(result, Is.EqualTo(AttributeWritability.ReadOnly));
    }

    [Test]
    public void DetermineAttributeWritability_ConstructedWithOtherFlags_ReturnsReadOnly()
    {
        // systemFlags = 0x5 (constructed + not-replicated)
        var result = LdapConnectorUtilities.DetermineAttributeWritability(false, 0x5, null);
        Assert.That(result, Is.EqualTo(AttributeWritability.ReadOnly));
    }

    [Test]
    public void DetermineAttributeWritability_NonConstructedFlags_ReturnsWritable()
    {
        // systemFlags = 0x1 (not-replicated only, no constructed bit)
        var result = LdapConnectorUtilities.DetermineAttributeWritability(false, 0x1, null);
        Assert.That(result, Is.EqualTo(AttributeWritability.Writable));
    }

    [Test]
    public void DetermineAttributeWritability_OddLinkId_ReturnsReadOnly()
    {
        // odd linkID = back-link attribute (e.g. memberOf)
        var result = LdapConnectorUtilities.DetermineAttributeWritability(false, null, 3);
        Assert.That(result, Is.EqualTo(AttributeWritability.ReadOnly));
    }

    [Test]
    public void DetermineAttributeWritability_EvenLinkId_ReturnsWritable()
    {
        // even linkID = forward-link attribute (e.g. member)
        var result = LdapConnectorUtilities.DetermineAttributeWritability(false, null, 2);
        Assert.That(result, Is.EqualTo(AttributeWritability.Writable));
    }

    [Test]
    public void DetermineAttributeWritability_AllNull_ReturnsWritable()
    {
        var result = LdapConnectorUtilities.DetermineAttributeWritability(null, null, null);
        Assert.That(result, Is.EqualTo(AttributeWritability.Writable));
    }

    [Test]
    public void DetermineAttributeWritability_AllDefaults_ReturnsWritable()
    {
        var result = LdapConnectorUtilities.DetermineAttributeWritability(false, 0, null);
        Assert.That(result, Is.EqualTo(AttributeWritability.Writable));
    }

    [Test]
    public void DetermineAttributeWritability_SystemOnlyTakesPrecedence()
    {
        // systemOnly=true should return ReadOnly even with writable flags otherwise
        var result = LdapConnectorUtilities.DetermineAttributeWritability(true, 0, 2);
        Assert.That(result, Is.EqualTo(AttributeWritability.ReadOnly));
    }

    #endregion

    #region HasValidRdnValues tests

    [Test]
    public void HasValidRdnValues_ValidDn_ReturnsTrue()
    {
        Assert.That(LdapConnectorUtilities.HasValidRdnValues("CN=John Smith,OU=Users,OU=Corp,DC=example,DC=local"), Is.True);
    }

    [Test]
    public void HasValidRdnValues_SingleComponent_ReturnsTrue()
    {
        Assert.That(LdapConnectorUtilities.HasValidRdnValues("DC=local"), Is.True);
    }

    [Test]
    public void HasValidRdnValues_EmptyOuComponent_ReturnsFalse()
    {
        // This is the key scenario: an empty RDN component like "OU=,OU=Users,..."
        Assert.That(LdapConnectorUtilities.HasValidRdnValues("CN=John Smith,OU=,OU=Users,OU=Corp,DC=example,DC=local"), Is.False);
    }

    [Test]
    public void HasValidRdnValues_EmptyCnComponent_ReturnsFalse()
    {
        Assert.That(LdapConnectorUtilities.HasValidRdnValues("CN=,OU=Users,DC=example,DC=local"), Is.False);
    }

    [Test]
    public void HasValidRdnValues_EmptyString_ReturnsFalse()
    {
        Assert.That(LdapConnectorUtilities.HasValidRdnValues(""), Is.False);
    }

    [Test]
    public void HasValidRdnValues_NullString_ReturnsFalse()
    {
        Assert.That(LdapConnectorUtilities.HasValidRdnValues(null!), Is.False);
    }

    [Test]
    public void HasValidRdnValues_MultipleEmptyComponents_ReturnsFalse()
    {
        Assert.That(LdapConnectorUtilities.HasValidRdnValues("CN=,OU=,DC=example,DC=local"), Is.False);
    }

    [Test]
    public void HasValidRdnValues_EscapedCommaInValue_ReturnsTrue()
    {
        // Escaped comma in value should be valid: "CN=Smith\, John"
        Assert.That(LdapConnectorUtilities.HasValidRdnValues(@"CN=Smith\, John,OU=Users,DC=example,DC=local"), Is.True);
    }

    [Test]
    public void HasValidRdnValues_WhitespaceOnlyValue_ReturnsFalse()
    {
        Assert.That(LdapConnectorUtilities.HasValidRdnValues("CN= ,OU=Users,DC=example,DC=local"), Is.False);
    }

    #endregion
}
