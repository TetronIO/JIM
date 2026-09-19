// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using NUnit.Framework;
using System.Text.Json;

namespace
 JIM.Worker.Tests.Connectors;

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
        var result = LdapConnectorUtilities.DetectDirectoryType(Array.Empty<string>(), "Acme Directory Services", null, "Acme/1.0");
        Assert.That(result, Is.EqualTo(LdapDirectoryType.Generic));
    }

    [Test]
    public void DetectDirectoryType_389ProjectVendorName_ReturnsDirectoryServer389()
    {
        var result = LdapConnectorUtilities.DetectDirectoryType(Array.Empty<string>(), "389 Project");
        Assert.That(result, Is.EqualTo(LdapDirectoryType.DirectoryServer389));
    }

    /// <summary>
    /// Red Hat Directory Server builds brand the vendor name differently but keep the 389-Directory version
    /// prefix, so the version alone must be enough.
    /// </summary>
    [Test]
    public void DetectDirectoryType_389DirectoryVendorVersionPrefixAlone_ReturnsDirectoryServer389()
    {
        var result = LdapConnectorUtilities.DetectDirectoryType(Array.Empty<string>(), "Red Hat, Inc.", null, "389-Directory/2.4.5 B2024.100.0000");
        Assert.That(result, Is.EqualTo(LdapDirectoryType.DirectoryServer389));
    }

    /// <summary>
    /// The AD capability OIDs win over everything else, as they do for Samba.
    /// </summary>
    [Test]
    public void DetectDirectoryType_AdCapabilityOidWith389VendorVersion_ReturnsActiveDirectory()
    {
        var capabilities = new[] { "1.2.840.113556.1.4.800" };
        var result = LdapConnectorUtilities.DetectDirectoryType(capabilities, null, null, "389-Directory/2.4.5");
        Assert.That(result, Is.EqualTo(LdapDirectoryType.ActiveDirectory));
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

    #region DirectoryServer389

    /// <summary>
    /// The enum is persisted as an integer inside PersistedConnectorData, so the position of every existing member
    /// is load-bearing: reordering would silently retype every Connected System's stored directory type.
    /// </summary>
    [Test]
    public void LdapDirectoryType_ExistingOrdinals_AreUnchangedAndDirectoryServer389IsAppended()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int)LdapDirectoryType.ActiveDirectory, Is.EqualTo(0));
            Assert.That((int)LdapDirectoryType.SambaAD, Is.EqualTo(1));
            Assert.That((int)LdapDirectoryType.OpenLDAP, Is.EqualTo(2));
            Assert.That((int)LdapDirectoryType.Generic, Is.EqualTo(3));
            Assert.That((int)LdapDirectoryType.DirectoryServer389, Is.EqualTo(4));
        }
    }

    /// <summary>
    /// For everything except password policy discovery, 389 Directory Server behaves as Generic did before it was
    /// recognised, so an existing deployment changes nothing except its label.
    /// </summary>
    [Test]
    public void ComputedProperties_DirectoryServer389_MatchGeneric()
    {
        var directoryServer = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.DirectoryServer389 };
        var generic = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.Generic };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(directoryServer.ExternalIdAttributeName, Is.EqualTo(generic.ExternalIdAttributeName));
            Assert.That(directoryServer.ExternalIdDataType, Is.EqualTo(generic.ExternalIdDataType));
            Assert.That(directoryServer.UseUsnDeltaImport, Is.EqualTo(generic.UseUsnDeltaImport));
            Assert.That(directoryServer.UseAccesslogDeltaImport, Is.EqualTo(generic.UseAccesslogDeltaImport));
            Assert.That(directoryServer.EnforcesSamSingleValuedRules, Is.EqualTo(generic.EnforcesSamSingleValuedRules));
            Assert.That(directoryServer.RecommendedExportConcurrency, Is.EqualTo(generic.RecommendedExportConcurrency));
            Assert.That(directoryServer.SupportsPaging, Is.EqualTo(generic.SupportsPaging));
            Assert.That(directoryServer.SupportsPaging, Is.True);
        }
    }

    [Test]
    public void ShouldOverridePlurality_DirectoryServer389_DescriptionOnUser_ReturnsFalse()
    {
        var result = LdapConnectorUtilities.ShouldOverridePluralityToSingleValued(
            "description", "user", LdapDirectoryType.DirectoryServer389);
        Assert.That(result, Is.False);
    }

    /// <summary>
    /// Persisted connector data written before the root DSE facts were extended carries none of the new
    /// properties, and must keep deserialising with those facts simply unknown.
    /// </summary>
    [Test]
    public void LdapConnectorRootDse_OldPersistedJsonWithoutTheNewFacts_DeserialisesWithThemNull()
    {
        const string oldJson = """{"DnsHostName":"ldap.example.local","DirectoryType":3,"VendorName":"Acme","NamingContexts":["dc=example,dc=local"]}""";

        var rootDse = JsonSerializer.Deserialize<LdapConnectorRootDse>(oldJson);

        Assert.That(rootDse, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootDse!.DirectoryType, Is.EqualTo(LdapDirectoryType.Generic));
            Assert.That(rootDse.NamingContexts, Is.EqualTo(new[] { "dc=example,dc=local" }));
            Assert.That(rootDse.VendorVersion, Is.Null);
            Assert.That(rootDse.ConfigContext, Is.Null);
            Assert.That(rootDse.SupportedControls, Is.Null);
            Assert.That(rootDse.DefaultNamingContext, Is.Null);
        }
    }

    [Test]
    public void LdapConnectorRootDse_NewFacts_RoundTripThroughJson()
    {
        var rootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.DirectoryServer389,
            VendorVersion = "389-Directory/2.4.5",
            ConfigContext = "cn=config",
            SupportedControls = ["1.3.6.1.4.1.42.2.27.8.5.1"],
            DefaultNamingContext = "dc=example,dc=local"
        };

        var roundTripped = JsonSerializer.Deserialize<LdapConnectorRootDse>(JsonSerializer.Serialize(rootDse));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roundTripped!.DirectoryType, Is.EqualTo(LdapDirectoryType.DirectoryServer389));
            Assert.That(roundTripped.VendorVersion, Is.EqualTo("389-Directory/2.4.5"));
            Assert.That(roundTripped.ConfigContext, Is.EqualTo("cn=config"));
            Assert.That(roundTripped.SupportedControls, Is.EqualTo(new[] { "1.3.6.1.4.1.42.2.27.8.5.1" }));
            Assert.That(roundTripped.DefaultNamingContext, Is.EqualTo("dc=example,dc=local"));
        }
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
