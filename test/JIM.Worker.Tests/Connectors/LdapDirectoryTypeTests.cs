using JIM.Connectors.LDAP;
using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class LdapDirectoryTypeTests
{
    #region RootDse computed properties — ExternalIdAttributeName

    [Test]
    public void ExternalIdAttributeName_ActiveDirectory_ReturnsObjectGUID()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };
        Assert.That(rootDse.ExternalIdAttributeName, Is.EqualTo("objectGUID"));
    }

    [Test]
    public void ExternalIdAttributeName_OpenLDAP_ReturnsEntryUUID()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };
        Assert.That(rootDse.ExternalIdAttributeName, Is.EqualTo("entryUUID"));
    }

    [Test]
    public void ExternalIdAttributeName_Generic_ReturnsEntryUUID()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.Generic };
        Assert.That(rootDse.ExternalIdAttributeName, Is.EqualTo("entryUUID"));
    }

    #endregion

    #region RootDse computed properties — ExternalIdDataType

    [Test]
    public void ExternalIdDataType_ActiveDirectory_ReturnsGuid()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };
        Assert.That(rootDse.ExternalIdDataType, Is.EqualTo(AttributeDataType.Guid));
    }

    [Test]
    public void ExternalIdDataType_OpenLDAP_ReturnsText()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };
        Assert.That(rootDse.ExternalIdDataType, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void ExternalIdDataType_Generic_ReturnsText()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.Generic };
        Assert.That(rootDse.ExternalIdDataType, Is.EqualTo(AttributeDataType.Text));
    }

    #endregion

    #region RootDse computed properties — UseUsnDeltaImport

    [Test]
    public void UseUsnDeltaImport_ActiveDirectory_ReturnsTrue()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };
        Assert.That(rootDse.UseUsnDeltaImport, Is.True);
    }

    [Test]
    public void UseUsnDeltaImport_OpenLDAP_ReturnsFalse()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };
        Assert.That(rootDse.UseUsnDeltaImport, Is.False);
    }

    [Test]
    public void UseUsnDeltaImport_Generic_ReturnsFalse()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.Generic };
        Assert.That(rootDse.UseUsnDeltaImport, Is.False);
    }

    #endregion

    #region RootDse computed properties — EnforcesSamSingleValuedRules

    [Test]
    public void EnforcesSamSingleValuedRules_ActiveDirectory_ReturnsTrue()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };
        Assert.That(rootDse.EnforcesSamSingleValuedRules, Is.True);
    }

    [Test]
    public void EnforcesSamSingleValuedRules_OpenLDAP_ReturnsFalse()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };
        Assert.That(rootDse.EnforcesSamSingleValuedRules, Is.False);
    }

    [Test]
    public void EnforcesSamSingleValuedRules_Generic_ReturnsFalse()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.Generic };
        Assert.That(rootDse.EnforcesSamSingleValuedRules, Is.False);
    }

    #endregion

    #region RootDse default state

    [Test]
    public void DirectoryType_DefaultsToGeneric()
    {
        var rootDse = new LdapConnectorRootDse();
        Assert.That(rootDse.DirectoryType, Is.EqualTo(LdapDirectoryType.Generic));
    }

    #endregion

    #region ShouldOverridePluralityToSingleValued

    [Test]
    public void ShouldOverridePlurality_ActiveDirectory_DescriptionOnUser_ReturnsTrue()
    {
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued(
            "description", "user", LdapDirectoryType.ActiveDirectory);
        Assert.That(result, Is.True);
    }

    [Test]
    public void ShouldOverridePlurality_OpenLDAP_DescriptionOnUser_ReturnsFalse()
    {
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued(
            "description", "user", LdapDirectoryType.OpenLDAP);
        Assert.That(result, Is.False);
    }

    [Test]
    public void ShouldOverridePlurality_ActiveDirectory_NonSamAttribute_ReturnsFalse()
    {
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued(
            "mail", "user", LdapDirectoryType.ActiveDirectory);
        Assert.That(result, Is.False);
    }

    [Test]
    public void ShouldOverridePlurality_ActiveDirectory_DescriptionOnNonSamClass_ReturnsFalse()
    {
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued(
            "description", "organizationalUnit", LdapDirectoryType.ActiveDirectory);
        Assert.That(result, Is.False);
    }

    #endregion

    #region DetectDirectoryType

    [Test]
    public void DetectDirectoryType_AdCapabilityOid_ReturnsActiveDirectory()
    {
        var capabilities = new[] { "1.2.840.113556.1.4.800" };
        var result = LdapConnectorUtilities.DetectDirectoryType(capabilities, null);
        Assert.That(result, Is.EqualTo(LdapDirectoryType.ActiveDirectory));
    }

    [Test]
    public void DetectDirectoryType_AdLdsCapabilityOid_ReturnsActiveDirectory()
    {
        var capabilities = new[] { "1.2.840.113556.1.4.1851" };
        var result = LdapConnectorUtilities.DetectDirectoryType(capabilities, null);
        Assert.That(result, Is.EqualTo(LdapDirectoryType.ActiveDirectory));
    }

    [Test]
    public void DetectDirectoryType_OpenLDAPVendorName_ReturnsOpenLDAP()
    {
        var result = LdapConnectorUtilities.DetectDirectoryType(null, "OpenLDAP Project");
        Assert.That(result, Is.EqualTo(LdapDirectoryType.OpenLDAP));
    }

    [Test]
    public void DetectDirectoryType_OpenLDAPVendorNameCaseInsensitive_ReturnsOpenLDAP()
    {
        var result = LdapConnectorUtilities.DetectDirectoryType(null, "openldap");
        Assert.That(result, Is.EqualTo(LdapDirectoryType.OpenLDAP));
    }

    [Test]
    public void DetectDirectoryType_NoCapabilitiesNoVendor_ReturnsGeneric()
    {
        var result = LdapConnectorUtilities.DetectDirectoryType(null, null);
        Assert.That(result, Is.EqualTo(LdapDirectoryType.Generic));
    }

    [Test]
    public void DetectDirectoryType_UnknownVendor_ReturnsGeneric()
    {
        var result = LdapConnectorUtilities.DetectDirectoryType(Array.Empty<string>(), "389 Directory Server");
        Assert.That(result, Is.EqualTo(LdapDirectoryType.Generic));
    }

    [Test]
    public void DetectDirectoryType_SambaAdWithAdOid_ReturnsSambaAD()
    {
        // Samba AD advertises the AD capability OID but has different behaviour
        var capabilities = new[] { "1.2.840.113556.1.4.800" };
        var result = LdapConnectorUtilities.DetectDirectoryType(capabilities, "Samba Team");
        Assert.That(result, Is.EqualTo(LdapDirectoryType.SambaAD));
    }

    [Test]
    public void DetectDirectoryType_OpenLDAPRootDseObjectClass_ReturnsOpenLDAP()
    {
        // OpenLDAP may not set vendorName but always uses OpenLDAProotDSE as the rootDSE structural object class
        var result = LdapConnectorUtilities.DetectDirectoryType(null, null, "OpenLDAProotDSE");
        Assert.That(result, Is.EqualTo(LdapDirectoryType.OpenLDAP));
    }

    [Test]
    public void DetectDirectoryType_VendorNameTakesPrecedenceOverObjectClass_ReturnsOpenLDAP()
    {
        // When both vendorName and structuralObjectClass indicate OpenLDAP, vendorName is checked first
        var result = LdapConnectorUtilities.DetectDirectoryType(null, "OpenLDAP", "OpenLDAProotDSE");
        Assert.That(result, Is.EqualTo(LdapDirectoryType.OpenLDAP));
    }

    #endregion

    #region SambaAD computed properties

    [Test]
    public void ExternalIdAttributeName_SambaAD_ReturnsObjectGUID()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.SambaAD };
        Assert.That(rootDse.ExternalIdAttributeName, Is.EqualTo("objectGUID"));
    }

    [Test]
    public void ExternalIdDataType_SambaAD_ReturnsGuid()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.SambaAD };
        Assert.That(rootDse.ExternalIdDataType, Is.EqualTo(AttributeDataType.Guid));
    }

    [Test]
    public void UseUsnDeltaImport_SambaAD_ReturnsTrue()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.SambaAD };
        Assert.That(rootDse.UseUsnDeltaImport, Is.True);
    }

    [Test]
    public void EnforcesSamSingleValuedRules_SambaAD_ReturnsTrue()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.SambaAD };
        Assert.That(rootDse.EnforcesSamSingleValuedRules, Is.True);
    }

    [Test]
    public void SupportsPaging_SambaAD_ReturnsFalse()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.SambaAD };
        Assert.That(rootDse.SupportsPaging, Is.False);
    }

    [Test]
    public void SupportsPaging_ActiveDirectory_ReturnsTrue()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };
        Assert.That(rootDse.SupportsPaging, Is.True);
    }

    [Test]
    public void SupportsPaging_OpenLDAP_ReturnsTrue()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };
        Assert.That(rootDse.SupportsPaging, Is.True);
    }

    [Test]
    public void ShouldOverridePlurality_SambaAD_DescriptionOnUser_ReturnsTrue()
    {
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued(
            "description", "user", LdapDirectoryType.SambaAD);
        Assert.That(result, Is.True);
    }

    #endregion
}
