// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A directory returns an operational attribute only when a search asks for it by name; a request for the user
/// attributes (or for none) leaves it out. So selecting an operational attribute (#2153, D8) only works because a
/// Full Import names every selected attribute in its search. These tests pin that.
/// </summary>
[TestFixture]
public class LdapConnectorImportRequestedAttributeTests
{
    [Test]
    public void GetRequestedAttributeNames_SelectedOperationalAttribute_IsRequestedByName()
    {
        var objectType = InetOrgPerson();

        var requested = LdapConnectorImport.GetRequestedAttributeNames(objectType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requested, Does.Contain("pwdFailureTime"), "A selected operational attribute must be named in the search.");
            Assert.That(requested, Does.Not.Contain("createTimestamp"), "An unselected one must not be fetched.");
            Assert.That(requested, Does.Contain("mail"));
            Assert.That(requested, Does.Contain("entryUUID"), "The External ID is always requested.");
            Assert.That(requested, Does.Contain("objectClass"), "objectClass is always requested for type matching.");
            Assert.That(requested, Does.Not.Contain("*").And.Not.Contain("+"),
                "Wildcards would fetch every operational attribute; only what is selected is requested.");
            Assert.That(requested, Is.Unique);
        }
    }

    /// <summary>
    /// A connector-defined Exchange attribute (#2021) is not a directory attribute, and its name, which contains spaces,
    /// is not even a valid one to search for. The search asks instead for the attributes Exchange stamped, from which
    /// the import derives the value.
    /// </summary>
    [Test]
    public void GetRequestedAttributeNames_SelectedExchangeAttribute_RequestsWhatItIsDerivedFromInstead()
    {
        var objectType = InetOrgPerson();
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Name = "Exchange Recipient Type", Type = AttributeDataType.Text, Selected = true, IsConnectorDefined = true, ConnectedSystemObjectType = objectType
        });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Name = "Exchange Primary SMTP Address", Type = AttributeDataType.Text, Selected = true, IsConnectorDefined = true, ConnectedSystemObjectType = objectType
        });

        var requested = LdapConnectorImport.GetRequestedAttributeNames(objectType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requested, Has.None.Contains(" "), "no requested attribute name may contain a space");
            Assert.That(requested, Does.Contain("msExchRecipientTypeDetails"));
            Assert.That(requested, Does.Contain("proxyAddresses"));
            Assert.That(requested, Does.Not.Contain("homeMDB"), "Exchange Database is not selected, so what it is derived from is not read");
            Assert.That(requested, Is.Unique);
        }
    }

    private static ConnectedSystemObjectType InetOrgPerson()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "inetOrgPerson", Selected = true };
        objectType.Attributes =
        [
            new() { Name = "entryUUID", Type = AttributeDataType.Text, IsExternalId = true, Selected = true, ConnectedSystemObjectType = objectType },
            new() { Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true, Selected = true, ConnectedSystemObjectType = objectType },
            new() { Name = "mail", Type = AttributeDataType.Text, Selected = true, ConnectedSystemObjectType = objectType },
            new()
            {
                Name = "pwdFailureTime", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.MultiValued,
                Writability = AttributeWritability.ReadOnly, Selected = true, IsOperational = true, ConnectedSystemObjectType = objectType
            },
            new()
            {
                Name = "createTimestamp", Type = AttributeDataType.DateTime, Writability = AttributeWritability.ReadOnly,
                Selected = false, IsOperational = true, ConnectedSystemObjectType = objectType
            }
        ];
        return objectType;
    }
}
