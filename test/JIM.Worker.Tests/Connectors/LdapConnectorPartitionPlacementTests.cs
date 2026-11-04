// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Interfaces;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector's answer to which Partition a directory object is in (#2723): the Partition whose distinguished
/// name is the longest suffix of the object's, compared RDN by RDN as LDAP compares names. It is the rule that judges
/// which Partition's Delta Import watermark covers a change (#2715), so the two cannot disagree about where an object is.
/// </summary>
[TestFixture]
public class LdapConnectorPartitionPlacementTests
{
    private static readonly ConnectedSystemPartition Corp = new() { Id = 1, Name = "corp.local", ExternalId = "DC=corp,DC=local", Selected = true };

    private static ConnectedSystemPartition? Place(string? distinguishedName, params ConnectedSystemPartition[] partitions)
    {
        using var connector = new LdapConnector();
        return ((IConnectorPartitionPlacement)connector).PreparePartitionPlacement(partitions).GetPartition(distinguishedName);
    }

    [Test]
    public void GetPartition_ObjectWithinThePartition_IsPlacedInIt()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Place("CN=Pat Smith,OU=Users,DC=corp,DC=local", Corp), Is.SameAs(Corp));
            Assert.That(Place("cn=pat smith, ou=users, dc=CORP, dc=Local", Corp), Is.SameAs(Corp),
                "attribute types and values are compared without regard to case, and the space some directories write after a separator is not part of a value");
            Assert.That(Place("DC=corp,DC=local", Corp), Is.SameAs(Corp), "the Partition's own entry is in it");
        }
    }

    [Test]
    public void GetPartition_ActiveDirectoryApplicationPartitionUnderTheDomain_ChoosesTheApplicationPartition()
    {
        // DC=DomainDnsZones sits beneath the domain's naming context, so its objects lie under both suffixes.
        var domainDnsZones = new ConnectedSystemPartition { Id = 2, Name = "DomainDnsZones.corp.local", ExternalId = "DC=DomainDnsZones,DC=corp,DC=local", Selected = true };
        const string dnsRecord = "DC=host1,DC=corp.local,CN=MicrosoftDNS,DC=DomainDnsZones,DC=corp,DC=local";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Place(dnsRecord, Corp, domainDnsZones), Is.SameAs(domainDnsZones));
            Assert.That(Place(dnsRecord, domainDnsZones, Corp), Is.SameAs(domainDnsZones), "whichever order the Partitions come in");
            Assert.That(Place("CN=Pat Smith,OU=Users,DC=corp,DC=local", Corp, domainDnsZones), Is.SameAs(Corp));
        }
    }

    [Test]
    public void GetPartition_OpenLdapSubordinateSuffix_ChoosesTheSubordinateSuffix()
    {
        // OpenLDAP and 389 Directory Server can host one database's suffix beneath another's.
        var corp = new ConnectedSystemPartition { Id = 1, Name = "corp", ExternalId = "dc=corp,dc=local", Selected = true };
        var partners = new ConnectedSystemPartition { Id = 2, Name = "partners", ExternalId = "ou=partners,dc=corp,dc=local", Selected = true };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Place("uid=pat,ou=people,ou=partners,dc=corp,dc=local", corp, partners), Is.SameAs(partners));
            Assert.That(Place("uid=pat,ou=people,ou=partners,dc=corp,dc=local", partners, corp), Is.SameAs(partners));
            Assert.That(Place("uid=cal,ou=people,dc=corp,dc=local", corp, partners), Is.SameAs(corp));
        }
    }

    [TestCase("CN=Pat Smith,OU=Users,DC=partners,DC=local", Description = "another naming context")]
    [TestCase("CN=Pat Smith,OU=Users,DC=notcorp,DC=local", Description = "a value that only ends like the Partition's")]
    [TestCase("CN=Pat Smith,ADC=corp,DC=local", Description = "an attribute type that only ends like the Partition's")]
    [TestCase("DC=local", Description = "the Partition's parent")]
    public void GetPartition_ObjectOutsideEveryPartition_ReturnsNull(string distinguishedName) =>
        Assert.That(Place(distinguishedName, Corp), Is.Null);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not a distinguished name")]
    [TestCase("=Pat,DC=corp,DC=local", Description = "an RDN with no attribute type")]
    public void GetPartition_UnreadableDistinguishedName_ReturnsNull(string? distinguishedName) =>
        Assert.That(Place(distinguishedName, Corp), Is.Null);

    [Test]
    public void GetPartition_EscapedCommaInTheLeafRdn_IsNotPlacedInALookAlikeSuffix()
    {
        // "CN=Smith\,DC=corp" is one RDN whose value holds a comma, so the object is a child of DC=local, not of
        // DC=corp,DC=local: a suffix comparison on the raw string would place it in corp.local.
        var local = new ConnectedSystemPartition { Id = 3, Name = "local", ExternalId = "DC=local", Selected = true };
        const string escaped = @"CN=Smith\,DC=corp,DC=local";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Place(escaped, Corp), Is.Null);
            Assert.That(Place(escaped, Corp, local), Is.SameAs(local));
            Assert.That(Place(@"CN=Smith\2C Pat,DC=corp,DC=local", Corp), Is.SameAs(Corp), "a hex-escaped comma in a value is still one RDN under corp.local");
        }
    }

    [Test]
    public void GetPartition_PartitionWithAnUnreadableDistinguishedName_HoldsNothing()
    {
        var unnamed = new ConnectedSystemPartition { Id = 4, Name = "unnamed", ExternalId = "", Selected = true };
        var garbled = new ConnectedSystemPartition { Id = 5, Name = "garbled", ExternalId = "not a distinguished name", Selected = true };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Place("CN=Pat Smith,OU=Users,DC=corp,DC=local", unnamed, garbled, Corp), Is.SameAs(Corp));
            Assert.That(Place("CN=Pat Smith,OU=Users,DC=other,DC=local", unnamed, garbled), Is.Null);
        }
    }

    [Test]
    public void GetPartition_NoPartitions_ReturnsNull() =>
        Assert.That(Place("CN=Pat Smith,OU=Users,DC=corp,DC=local"), Is.Null);

    [Test]
    public void PartitionForDn_AgreesWithThePlacer()
    {
        // The watermark rule (#2715) and the placement rule are one rule: a change is read against the watermark of the
        // Partition its object is placed in.
        var partners = new ConnectedSystemPartition { Id = 2, Name = "partners", ExternalId = "OU=partners,DC=corp,DC=local", Selected = true };
        ConnectedSystemPartition[] partitions = [Corp, partners];

        foreach (var dn in new[] { "uid=pat,ou=partners,dc=corp,dc=local", "uid=cal,dc=corp,dc=local", "uid=ann,dc=other,dc=local", @"CN=Smith\,DC=corp,DC=local" })
            Assert.That(LdapConnectorUtilities.PartitionForDn(dn, partitions), Is.SameAs(Place(dn, partitions)), dn);
    }
}
