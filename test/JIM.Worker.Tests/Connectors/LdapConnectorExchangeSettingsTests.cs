// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector's Exchange settings (#2021, PRD requirement 1): Exchange recipients can be managed only on Active
/// Directory, so turning it on against any other directory is refused when the settings are saved.
/// </summary>
[TestFixture]
public class LdapConnectorExchangeSettingsTests
{
    // The directory type is internal to the Connector, so cases name it.
    [TestCase("SambaAD")]
    [TestCase("OpenLDAP")]
    [TestCase("DirectoryServer389")]
    [TestCase("Generic")]
    public void DirectoryTypeRefusal_ManagingExchangeOnAnotherDirectory_RefusesWithTheReason(string directoryType)
    {
        var refusal = LdapExchangeSettings.DirectoryTypeRefusal(manageExchangeRecipients: true, Enum.Parse<LdapDirectoryType>(directoryType));

        Assert.That(refusal, Does.Contain("Active Directory"));
    }

    [Test]
    public void DirectoryTypeRefusal_ManagingExchangeOnActiveDirectory_Allows()
    {
        Assert.That(LdapExchangeSettings.DirectoryTypeRefusal(manageExchangeRecipients: true, LdapDirectoryType.ActiveDirectory), Is.Null);
    }

    [TestCase("SambaAD")]
    [TestCase("ActiveDirectory")]
    public void DirectoryTypeRefusal_NotManagingExchange_NeverRefuses(string directoryType)
    {
        Assert.That(LdapExchangeSettings.DirectoryTypeRefusal(manageExchangeRecipients: false, Enum.Parse<LdapDirectoryType>(directoryType)), Is.Null);
    }
}
