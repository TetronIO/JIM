// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// Which Partition JIM records for a Connected System Object (#2723). The Connector says where an object is; JIM prefers
/// the most specific selected Partition holding it, then any Partition, and has no answer for an object the Connector
/// cannot place, or for a Connector that cannot place objects at all.
/// </summary>
[TestFixture]
public class ConnectedSystemPartitionPlacementTests
{
    private static ConnectedSystemPartition Partition(int id, string distinguishedName, bool selected = true) =>
        new() { Id = id, Name = distinguishedName, ExternalId = distinguishedName, Selected = selected };

    [Test]
    public void Place_InnerDeselectedSuffixUnderASelectedOne_PrefersTheSelectedOuter()
    {
        // An Active Directory application partition, or an OpenLDAP subordinate suffix, the administrator left out: the
        // object lies under both, and only the selected one is read by any Run Profile, so only it can see the object go.
        var corp = Partition(1, "DC=corp,DC=local");
        var dnsZones = Partition(2, "DC=DomainDnsZones,DC=corp,DC=local", selected: false);
        var placement = ConnectedSystemPartitionPlacement.Prepare(new DistinguishedNameSuffixPlacement(), [corp, dnsZones]);

        Assert.That(placement!.Place("DC=host1,DC=DomainDnsZones,DC=corp,DC=local"), Is.SameAs(corp));
    }

    [Test]
    public void Place_NestedSuffixesBothSelected_ChoosesTheMostSpecific()
    {
        var corp = Partition(1, "DC=corp,DC=local");
        var dnsZones = Partition(2, "DC=DomainDnsZones,DC=corp,DC=local");
        var placement = ConnectedSystemPartitionPlacement.Prepare(new DistinguishedNameSuffixPlacement(), [corp, dnsZones]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(placement!.Place("DC=host1,DC=DomainDnsZones,DC=corp,DC=local"), Is.SameAs(dnsZones));
            Assert.That(placement.Place("CN=Pat,DC=corp,DC=local"), Is.SameAs(corp));
        }
    }

    [Test]
    public void Place_NoSelectedPartitionHoldsTheObject_FallsBackToAnyPartition()
    {
        // Placed truthfully even where no Run Profile reads it, rather than not at all.
        var corp = Partition(1, "DC=corp,DC=local");
        var partners = Partition(2, "DC=partners,DC=local", selected: false);
        var placement = ConnectedSystemPartitionPlacement.Prepare(new DistinguishedNameSuffixPlacement(), [corp, partners]);

        Assert.That(placement!.Place("CN=Pat,DC=partners,DC=local"), Is.SameAs(partners));
    }

    [Test]
    public void Place_NoPartitionSelected_StillPlacesTheObject()
    {
        var partners = Partition(2, "DC=partners,DC=local", selected: false);
        var placement = ConnectedSystemPartitionPlacement.Prepare(new DistinguishedNameSuffixPlacement(), [partners]);

        Assert.That(placement!.Place("CN=Pat,DC=partners,DC=local"), Is.SameAs(partners));
    }

    [TestCase("CN=Pat,DC=elsewhere,DC=local")]
    [TestCase("")]
    [TestCase(null)]
    public void Place_ObjectInNoPartition_ReturnsNull(string? objectIdentifier)
    {
        var placement = ConnectedSystemPartitionPlacement.Prepare(new DistinguishedNameSuffixPlacement(),
            [Partition(1, "DC=corp,DC=local"), Partition(2, "DC=partners,DC=local", selected: false)]);

        Assert.That(placement!.Place(objectIdentifier), Is.Null);
    }

    [Test]
    public void Prepare_ConnectorWithoutTheCapability_ReturnsNull()
    {
        // No answer, so the import keeps today's rule: an object is recorded against its Run Profile's Partition.
        Assert.That(ConnectedSystemPartitionPlacement.Prepare(new ConnectorWithoutPlacement(), [Partition(1, "DC=corp,DC=local")]), Is.Null);
    }

    [Test]
    public void Prepare_ConnectedSystemWithNoPartitions_ReturnsNull()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ConnectedSystemPartitionPlacement.Prepare(new DistinguishedNameSuffixPlacement(), []), Is.Null);
            Assert.That(ConnectedSystemPartitionPlacement.Prepare(new DistinguishedNameSuffixPlacement(), null), Is.Null);
        }
    }

    [Test]
    public void Place_ManyObjects_PreparesTheConnectorOnlyUpFront()
    {
        // The Connector parses its Partitions when prepared, so preparing per object would parse them per object.
        var connector = new DistinguishedNameSuffixPlacement();
        var placement = ConnectedSystemPartitionPlacement.Prepare(connector,
            [Partition(1, "DC=corp,DC=local"), Partition(2, "DC=partners,DC=local", selected: false)]);

        for (var i = 0; i < 100; i++)
            placement!.Place($"CN=User {i},DC=partners,DC=local");

        Assert.That(connector.PreparedCount, Is.LessThanOrEqualTo(2), "once for the selected Partitions and once for them all");
    }

    [Test]
    public void Place_ObjectInASelectedPartition_AsksTheConnectorOnce()
    {
        // The common case, for an import reads selected Partitions: the fallback to every Partition is not asked.
        var connector = new DistinguishedNameSuffixPlacement();
        var placement = ConnectedSystemPartitionPlacement.Prepare(connector,
            [Partition(1, "DC=corp,DC=local"), Partition(2, "DC=partners,DC=local", selected: false)]);

        placement!.Place("CN=Pat,DC=corp,DC=local");

        Assert.That(connector.PlacedCount, Is.EqualTo(1));
    }

    private sealed class ConnectorWithoutPlacement : IConnector
    {
        public string Name => "Plain Connector";
        public string? Description => null;
        public string? Url => null;
    }
}
