// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The Active Directory attributes only Exchange may set (#2755). Since Exchange 2007, writing them over LDAP creates no
/// recipient: there is no Recipient Update Service to act on them, so a stamped <c>homeMDB</c> or
/// <c>msExchRecipientTypeDetails</c> leaves an object that looks mail-enabled with no mailbox behind it. The LDAP
/// Connector declares them read-only, so no export Attribute Flow can target them; they are still imported.
/// </summary>
[TestFixture]
public class LdapExchangeOwnedAttributesTests
{
    [TestCase("msExchRecipientTypeDetails")]
    [TestCase("msExchRecipientDisplayType")]
    [TestCase("msExchRemoteRecipientType")]
    [TestCase("homeMDB")]
    [TestCase("homeMTA")]
    [TestCase("msExchHomeServerName")]
    [TestCase("legacyExchangeDN")]
    public void Declare_AttributeOnlyExchangeMaySet_BecomesReadOnlyAndSaysExchangeSetsIt(string name)
    {
        var attribute = Writable(name);

        LdapExchangeOwnedAttributes.Declare(attribute);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.Writability, Is.EqualTo(AttributeWritability.ReadOnly));
            Assert.That(attribute.Description, Does.Contain("Set by Exchange"));
        }
    }

    [TestCase("HOMEMDB")]
    [TestCase("msexchrecipienttypedetails")]
    public void Declare_NameInAnotherCase_IsStillRecognised(string name)
    {
        // LDAP attribute names ignore case, so a directory that reports one in another case names the same attribute.
        var attribute = Writable(name);

        LdapExchangeOwnedAttributes.Declare(attribute);

        Assert.That(attribute.Writability, Is.EqualTo(AttributeWritability.ReadOnly));
    }

    [TestCase("mailNickname")]
    [TestCase("proxyAddresses")]
    [TestCase("targetAddress")]
    [TestCase("msExchHideFromAddressLists")]
    [TestCase("msExchMailboxGuid")]
    [TestCase("msExchArchiveGUID")]
    [TestCase("displayName")]
    public void Declare_AttributeThatMayBeWrittenDirectly_KeepsItsWritabilityAndDescription(string name)
    {
        // Supported to write on an existing recipient, or written by identity-sync tools when preparing a mail user
        // for a cross-forest move (the two GUIDs); none of them alone makes a half-provisioned recipient.
        var attribute = Writable(name);

        LdapExchangeOwnedAttributes.Declare(attribute);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.Writability, Is.EqualTo(AttributeWritability.Writable));
            Assert.That(attribute.Description, Is.EqualTo("From the directory"));
        }
    }

    private static ConnectorSchemaAttribute Writable(string name) =>
        new(name, AttributeDataType.Text, AttributePlurality.SingleValued) { Description = "From the directory" };
}
