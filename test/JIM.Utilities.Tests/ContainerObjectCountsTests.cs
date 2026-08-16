// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using JIM.Models.Staging;
using JIM.Utilities;
using NUnit.Framework;

namespace JIM.Utilities.Tests;

/// <summary>
/// Turning the direct object counts a Connector reports into the figures a Container row shows (#1276).
/// </summary>
/// <remarks>
/// The Connector counts what its own search returned, bucketed by the Container each object sits directly in.
/// Everything an administrator reads on the Partitions and Containers tab is derived from that here: a
/// <see cref="ConnectedSystemContainerScope.OneLevel"/> Container reports its own bucket, and a
/// <see cref="ConnectedSystemContainerScope.Subtree"/> one reports its bucket plus every descendant's.
///
/// Deliberately blind to selections and exclusions. The count answers "what is in there?", which is the question
/// being asked while the selection is still being decided; what JIM would actually import once exclusions are
/// applied is the Configuration Change Preview's job (#1251), and answering it twice in two places is how the two
/// come to disagree.
/// </remarks>
[TestFixture]
public class ContainerObjectCountsTests
{
    [Test]
    public void Apply_ContainerWithObjectsDirectlyInIt_ReportsThatCountAsItsDirectTotal()
    {
        var partition = Partition(Container("ou=People,dc=corp"));

        ContainerObjectCounts.Apply(partition, new Dictionary<string, int> { ["ou=People,dc=corp"] = 42 });

        var people = Find(partition, "ou=People,dc=corp");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(people.ObjectCount, Is.EqualTo(42));
            Assert.That(people.SubtreeObjectCount, Is.EqualTo(42), "with no children, the subtree total is its own count");
        }
    }

    [Test]
    public void Apply_ContainerWithNoObjects_ReportsZeroRatherThanNull()
    {
        // Absent and empty read identically on screen, and only one of them is true. A Container the Connector
        // searched and found nothing in must say zero; null is reserved for "not counted".
        var partition = Partition(Container("ou=Empty,dc=corp"));

        ContainerObjectCounts.Apply(partition, new Dictionary<string, int>());

        Assert.That(Find(partition, "ou=Empty,dc=corp").ObjectCount, Is.Zero);
    }

    [Test]
    public void Apply_ParentOfPopulatedChildren_SubtreeTotalAddsThemUpButDirectCountDoesNot()
    {
        var partition = Partition(
            Container("ou=Corp,dc=corp",
                Container("ou=Sales,ou=Corp,dc=corp"),
                Container("ou=Finance,ou=Corp,dc=corp")));

        ContainerObjectCounts.Apply(partition, new Dictionary<string, int>
        {
            ["ou=Corp,dc=corp"] = 3,
            ["ou=Sales,ou=Corp,dc=corp"] = 10,
            ["ou=Finance,ou=Corp,dc=corp"] = 7
        });

        var corp = Find(partition, "ou=Corp,dc=corp");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp.ObjectCount, Is.EqualTo(3), "only the objects sitting directly in it");
            Assert.That(corp.SubtreeObjectCount, Is.EqualTo(20), "its own three, plus ten and seven beneath it");
        }
    }

    [Test]
    public void Apply_DeepHierarchy_SubtreeTotalReachesEveryLevel()
    {
        var partition = Partition(
            Container("ou=A,dc=corp",
                Container("ou=B,ou=A,dc=corp",
                    Container("ou=C,ou=B,ou=A,dc=corp"))));

        ContainerObjectCounts.Apply(partition, new Dictionary<string, int>
        {
            ["ou=A,dc=corp"] = 1,
            ["ou=B,ou=A,dc=corp"] = 2,
            ["ou=C,ou=B,ou=A,dc=corp"] = 4
        });

        Assert.That(Find(partition, "ou=A,dc=corp").SubtreeObjectCount, Is.EqualTo(7));
    }

    [Test]
    public void Apply_CountForAContainerNotInTheHierarchy_RollsUpIntoItsNearestKnownAncestor()
    {
        // The hierarchy query and the counting query run against the same directory in the same refresh, but a
        // Connector may still return objects sitting under a Container it does not publish as part of the tree
        // (a hidden or system container). Dropping those would make a parent's subtree total quietly disagree
        // with what an import from it would bring back.
        var partition = Partition(Container("ou=Corp,dc=corp"));

        ContainerObjectCounts.Apply(partition, new Dictionary<string, int>
        {
            ["ou=Corp,dc=corp"] = 2,
            ["cn=Hidden,ou=Corp,dc=corp"] = 5
        });

        var corp = Find(partition, "ou=Corp,dc=corp");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp.ObjectCount, Is.EqualTo(2), "the unknown Container's objects are not directly in this one");
            Assert.That(corp.SubtreeObjectCount, Is.EqualTo(7), "but they are beneath it");
        }
    }

    [Test]
    public void Apply_CountForAnUndiscoveredContainer_ChoosesTheDeepestAncestorNotTheFirstOneFound()
    {
        // Every Container above it is an ancestor, so "an ancestor" is not good enough: the objects must land on
        // the lowest one, or a One Level row somewhere up the tree inherits objects that are nowhere near it.
        var partition = Partition(
            Container("ou=A,dc=corp",
                Container("ou=B,ou=A,dc=corp",
                    Container("ou=C,ou=B,ou=A,dc=corp"))));

        ContainerObjectCounts.Apply(partition, new Dictionary<string, int>
        {
            ["cn=Hidden,ou=C,ou=B,ou=A,dc=corp"] = 5
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Find(partition, "ou=C,ou=B,ou=A,dc=corp").ObjectCount, Is.Zero,
                "the hidden Container's objects do not sit directly in C");
            Assert.That(Find(partition, "ou=C,ou=B,ou=A,dc=corp").SubtreeObjectCount, Is.EqualTo(5));
            Assert.That(Find(partition, "ou=B,ou=A,dc=corp").SubtreeObjectCount, Is.EqualTo(5));
            Assert.That(Find(partition, "ou=A,dc=corp").SubtreeObjectCount, Is.EqualTo(5));
        }
    }

    [Test]
    public void Apply_CountForAContainerInAnotherPartition_IsDropped()
    {
        var partition = Partition(Container("ou=Corp,dc=corp"));

        ContainerObjectCounts.Apply(partition, new Dictionary<string, int>
        {
            ["ou=Corp,dc=corp"] = 2,
            ["ou=Elsewhere,dc=other"] = 99
        });

        Assert.That(Find(partition, "ou=Corp,dc=corp").SubtreeObjectCount, Is.EqualTo(2));
    }

    [Test]
    public void Apply_ASiblingWhoseIdentifierSharesAPrefix_IsNotTreatedAsAnAncestor()
    {
        // ou=NotCorp,dc=corp ends with "Corp,dc=corp" as plain text, and only the separator boundary tells the
        // two apart.
        var partition = Partition(
            Container("ou=Corp,dc=corp"),
            Container("ou=NotCorp,dc=corp"));

        ContainerObjectCounts.Apply(partition, new Dictionary<string, int>
        {
            ["cn=Hidden,ou=NotCorp,dc=corp"] = 6
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Find(partition, "ou=Corp,dc=corp").SubtreeObjectCount, Is.Zero);
            Assert.That(Find(partition, "ou=NotCorp,dc=corp").SubtreeObjectCount, Is.EqualTo(6));
        }
    }

    [Test]
    public void Apply_IsBlindToSelectionAndExclusion()
    {
        // The figure states what the Connected System holds. What JIM would import once exclusions apply is the
        // preview's answer, and a count that quietly subtracted an exclusion would contradict it.
        var partition = Partition(
            Container("ou=Corp,dc=corp",
                Container("ou=Service,ou=Corp,dc=corp", excluded: true)));

        ContainerObjectCounts.Apply(partition, new Dictionary<string, int>
        {
            ["ou=Corp,dc=corp"] = 4,
            ["ou=Service,ou=Corp,dc=corp"] = 9
        });

        Assert.That(Find(partition, "ou=Corp,dc=corp").SubtreeObjectCount, Is.EqualTo(13));
    }

    [Test]
    public void Apply_NoCountsAtAll_LeavesEveryContainerUncounted()
    {
        // A Connector that cannot answer leaves the column absent, rather than filling the tab with zeroes that
        // read as "this Container is empty".
        var partition = Partition(Container("ou=People,dc=corp"));

        ContainerObjectCounts.Apply(partition, directCountsByContainerIdentifier: null);

        var people = Find(partition, "ou=People,dc=corp");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(people.ObjectCount, Is.Null);
            Assert.That(people.SubtreeObjectCount, Is.Null);
        }
    }

    [Test]
    public void Apply_IdentifiersDifferingOnlyByCase_AreTreatedAsTheSameContainer()
    {
        // A directory's Distinguished Names are case-insensitive, and a Connector has no reason to normalise the
        // case it reports against the case JIM stored at discovery.
        var partition = Partition(Container("OU=People,DC=corp"));

        ContainerObjectCounts.Apply(partition, new Dictionary<string, int> { ["ou=people,dc=corp"] = 11 });

        Assert.That(Find(partition, "OU=People,DC=corp").ObjectCount, Is.EqualTo(11));
    }

    [Test]
    public void RecalculateSubtreeTotals_ContainersLoadedFromTheDatabase_RebuildsTheSubtreeTotals()
    {
        // ObjectCount persists; SubtreeObjectCount does not, because it is derivable and storing it would let the
        // two disagree. Anything that loads a hierarchy has to rebuild it, or a Subtree Container reports its own
        // direct count and understates its branch by however much sits beneath it.
        var partition = Partition(
            Container("ou=Corp,dc=corp",
                Container("ou=Sales,ou=Corp,dc=corp")));

        Find(partition, "ou=Corp,dc=corp").ObjectCount = 3;
        Find(partition, "ou=Sales,ou=Corp,dc=corp").ObjectCount = 10;

        ContainerObjectCounts.RecalculateSubtreeTotals(partition);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Find(partition, "ou=Corp,dc=corp").SubtreeObjectCount, Is.EqualTo(13));
            Assert.That(Find(partition, "ou=Sales,ou=Corp,dc=corp").SubtreeObjectCount, Is.EqualTo(10));
        }
    }

    [Test]
    public void RecalculateSubtreeTotals_AnUncountedHierarchy_LeavesEveryTotalNull()
    {
        var partition = Partition(Container("ou=Corp,dc=corp", Container("ou=Sales,ou=Corp,dc=corp")));

        ContainerObjectCounts.RecalculateSubtreeTotals(partition);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Find(partition, "ou=Corp,dc=corp").SubtreeObjectCount, Is.Null);
            Assert.That(Find(partition, "ou=Sales,ou=Corp,dc=corp").SubtreeObjectCount, Is.Null);
        }
    }

    [Test]
    public void RecalculateSubtreeTotals_ACountedParentOfUncountedChildren_TotalsOnlyWhatWasCounted()
    {
        // A Container added to the directory since the last count has no figure of its own. Treating that as zero
        // is right for the total (nothing is known to be in it) but the parent must still report a total, because
        // it was itself counted.
        var partition = Partition(Container("ou=Corp,dc=corp", Container("ou=New,ou=Corp,dc=corp")));
        Find(partition, "ou=Corp,dc=corp").ObjectCount = 4;

        ContainerObjectCounts.RecalculateSubtreeTotals(partition);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Find(partition, "ou=Corp,dc=corp").SubtreeObjectCount, Is.EqualTo(4));
            Assert.That(Find(partition, "ou=New,ou=Corp,dc=corp").SubtreeObjectCount, Is.Null,
                "an uncounted Container has nothing to report, and zero would read as \"empty\"");
        }
    }

    private static ConnectedSystemPartition Partition(params ConnectedSystemContainer[] containers)
    {
        var partition = new ConnectedSystemPartition { Id = 1, Name = "dc=corp", ExternalId = "dc=corp", Containers = [] };
        foreach (var container in containers)
            partition.Containers.Add(container);

        return partition;
    }

    private static ConnectedSystemContainer Container(string externalId, params ConnectedSystemContainer[] children) =>
        Container(externalId, excluded: false, children);

    private static ConnectedSystemContainer Container(string externalId, bool excluded, params ConnectedSystemContainer[] children)
    {
        var container = new ConnectedSystemContainer
        {
            ExternalId = externalId,
            Name = externalId,
            Excluded = excluded
        };

        foreach (var child in children)
            container.AddChildContainer(child);

        return container;
    }

    private static ConnectedSystemContainer Find(ConnectedSystemPartition partition, string externalId)
    {
        var found = FindIn(partition.Containers!, externalId);
        Assert.That(found, Is.Not.Null, $"no Container in the test hierarchy with external id {externalId}");
        return found!;
    }

    private static ConnectedSystemContainer? FindIn(IEnumerable<ConnectedSystemContainer> containers, string externalId)
    {
        foreach (var container in containers)
        {
            if (container.ExternalId == externalId)
                return container;

            var found = FindIn(container.ChildContainers, externalId);
            if (found != null)
                return found;
        }

        return null;
    }
}
