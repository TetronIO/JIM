// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Preview;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// The one membership question import, export and preview all ask.
/// </summary>
/// <remarks>
/// Written as the baseline for #1255: the resolution beneath <see cref="ConnectedSystemScope.Contains"/> changes
/// from an OR across the selected Containers to the most specific Container having the final say, and these pin
/// every answer it gives, including the undetermined (<c>null</c>) ones, so that change is provably behaviour-
/// preserving. The <c>null</c> cases matter most: a preview that resolved undetermined to "out of scope" would
/// count objects as leaving that may not be.
/// </remarks>
[TestFixture]
public class ConnectedSystemScopeTests
{
    [Test]
    public void Contains_NoPartitionOnTheObject_ReturnsUndetermined()
    {
        var scope = ScopeOver(SystemWithContainers(), selectedPartitionIds: [1], selectedContainerIds: [10]);

        Assert.That(scope.Contains(null, "CN=Alice,OU=Corp,DC=example,DC=local"), Is.Null);
    }

    [Test]
    public void Contains_PartitionNotSelected_ReturnsFalse()
    {
        var scope = ScopeOver(SystemWithContainers(), selectedPartitionIds: [], selectedContainerIds: [10]);

        Assert.That(scope.Contains(1, "CN=Alice,OU=Corp,DC=example,DC=local"), Is.False);
    }

    [Test]
    public void Contains_ContainersDoNotDecideScope_ReturnsTrueForASelectedPartition()
    {
        // A Connector with partitions but no Containers: the selected partition is the whole answer.
        var connectedSystem = SystemWithContainers();
        connectedSystem.ConnectorDefinition!.SupportsPartitionContainers = false;

        var scope = ScopeOver(connectedSystem, selectedPartitionIds: [1], selectedContainerIds: []);

        Assert.That(scope.Contains(1, "CN=Alice,OU=Anywhere,DC=example,DC=local"), Is.True);
    }

    [Test]
    public void Contains_SelectedPartitionWithNoSelectedContainers_ReturnsFalse()
    {
        // Determined, not undetermined: an administrator who has just cleared every Container needs that counted.
        var scope = ScopeOver(SystemWithContainers(), selectedPartitionIds: [1], selectedContainerIds: []);

        Assert.That(scope.Contains(1, "CN=Alice,OU=Corp,DC=example,DC=local"), Is.False);
    }

    [Test]
    public void Contains_ConnectorCannotExpressContainment_ReturnsUndetermined()
    {
        var scope = ConnectedSystemScope.From(
            SystemWithContainers(),
            new ConnectedSystemScopeSelectionProposal([1], [10]),
            containment: null);

        Assert.That(scope.Contains(1, "CN=Alice,OU=Corp,DC=example,DC=local"), Is.Null);
    }

    [Test]
    public void Contains_ObjectCarriesNoIdentifier_ReturnsUndetermined()
    {
        var scope = ScopeOver(SystemWithContainers(), selectedPartitionIds: [1], selectedContainerIds: [10]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.Contains(1, null), Is.Null);
            Assert.That(scope.Contains(1, string.Empty), Is.Null);
        }
    }

    [Test]
    public void Contains_ObjectInASelectedContainer_ReturnsTrue()
    {
        var scope = ScopeOver(SystemWithContainers(), selectedPartitionIds: [1], selectedContainerIds: [10]);

        Assert.That(scope.Contains(1, "CN=Alice,OU=Corp,DC=example,DC=local"), Is.True);
    }

    [Test]
    public void Contains_ObjectOutsideEverySelectedContainer_ReturnsFalse()
    {
        var scope = ScopeOver(SystemWithContainers(), selectedPartitionIds: [1], selectedContainerIds: [10]);

        Assert.That(scope.Contains(1, "CN=Alice,OU=Partners,DC=example,DC=local"), Is.False);
    }

    [Test]
    public void Contains_ObjectInANestedSelectedContainer_ReturnsTrue()
    {
        // Both Corp and Sales admit this object. Under the OR this passed because Corp matched; under most-specific
        // resolution it passes because Sales does. The answer must not move.
        var scope = ScopeOver(SystemWithContainers(), selectedPartitionIds: [1], selectedContainerIds: [10, 11]);

        Assert.That(scope.Contains(1, "CN=Alice,OU=Sales,OU=Corp,DC=example,DC=local"), Is.True);
    }

    [Test]
    public void Contains_ObjectBeneathAOneLevelContainerOnly_ReturnsFalse()
    {
        var connectedSystem = SystemWithContainers(corpScope: ConnectedSystemContainerScope.OneLevel);
        var scope = ScopeOver(connectedSystem, selectedPartitionIds: [1], selectedContainerIds: [10]);

        Assert.That(scope.Contains(1, "CN=Alice,OU=Sales,OU=Corp,DC=example,DC=local"), Is.False);
    }

    [Test]
    public void Contains_ObjectFromAnUnselectedPartitionsContainer_ReturnsFalse()
    {
        // Containers are collected from selected partitions only, so a Container id from elsewhere contributes nothing.
        var scope = ScopeOver(SystemWithContainers(), selectedPartitionIds: [1], selectedContainerIds: [10, 20]);

        Assert.That(scope.Contains(2, "CN=Alice,OU=Archive,DC=example,DC=local"), Is.False);
    }

    #region Exclusions (#1255)

    // The preview and the import must answer alike, which is the whole reason this type exists. An exclusion the
    // import honours and the preview does not is a preview stating a count the next import contradicts.

    [Test]
    public void Contains_ObjectWithinAnExcludedBranchOfASelectedContainer_ReturnsFalse()
    {
        var scope = ScopeOver(SystemWithContainers(), selectedPartitionIds: [1], selectedContainerIds: [10], excludedContainerIds: [11]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.Contains(1, "CN=Alice,OU=Sales,OU=Corp,DC=example,DC=local"), Is.False);
            Assert.That(scope.Contains(1, "CN=Bob,OU=Corp,DC=example,DC=local"), Is.True);
        }
    }

    [Test]
    public void Contains_ExclusionProposedOnAContainerNotYetExcluded_HonoursTheProposal()
    {
        // The proposal is the parameter, not the Connected System's stored flags: every Container in
        // SystemWithContainers is stored as Selected, and proposing to exclude one has to take effect here or the
        // preview would report on the configuration the administrator is trying to move away from.
        var scope = ScopeOver(SystemWithContainers(), selectedPartitionIds: [1], selectedContainerIds: [10], excludedContainerIds: [11]);

        Assert.That(scope.Contains(1, "CN=Alice,OU=Sales,OU=Corp,DC=example,DC=local"), Is.False);
    }

    [Test]
    public void Contains_ContainerExcludedOnTheConnectedSystemButSelectedInTheProposal_HonoursTheProposal()
    {
        // The mirror image: an administrator proposing to lift an existing exclusion.
        var connectedSystem = SystemWithContainers();
        var sales = connectedSystem.Partitions![0].Containers!.Single(c => c.Id == 10).ChildContainers.Single();
        sales.Selected = false;
        sales.Excluded = true;

        var scope = ScopeOver(connectedSystem, selectedPartitionIds: [1], selectedContainerIds: [10, 11]);

        Assert.That(scope.Contains(1, "CN=Alice,OU=Sales,OU=Corp,DC=example,DC=local"), Is.True);
    }

    [Test]
    public void Contains_OnlyAnExclusionSelected_ReturnsFalse()
    {
        // An exclusion with nothing selected around it manages nothing, which is a determined answer.
        var scope = ScopeOver(SystemWithContainers(), selectedPartitionIds: [1], selectedContainerIds: [], excludedContainerIds: [11]);

        Assert.That(scope.Contains(1, "CN=Alice,OU=Sales,OU=Corp,DC=example,DC=local"), Is.False);
    }

    [Test]
    public void Contains_ExcludedContainerInADeselectedPartition_IsNotConsulted()
    {
        // Containers are collected from selected partitions only, and an exclusion is no different: a partition
        // nobody imports from decides nothing about the partition they do.
        var scope = ScopeOver(SystemWithContainers(), selectedPartitionIds: [1], selectedContainerIds: [10], excludedContainerIds: [20]);

        Assert.That(scope.Contains(1, "CN=Alice,OU=Sales,OU=Corp,DC=example,DC=local"), Is.True);
    }

    [Test]
    public void FromCurrentSelection_ContainerExcludedOnTheConnectedSystem_CarriesTheExclusion()
    {
        // What "no change" looks like, and the baseline every preview is evaluated against. An exclusion missing
        // from the baseline reads as an exclusion the proposal is adding.
        var connectedSystem = SystemWithContainers();
        var sales = connectedSystem.Partitions![0].Containers!.Single(c => c.Id == 10).ChildContainers.Single();
        sales.Selected = false;
        sales.Excluded = true;

        var current = ConnectedSystemScopeSelectionProposal.FromCurrentSelection(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(current.ExcludedContainerIds, Is.EquivalentTo(new[] { 11 }));

            // Every partition's Containers, not just the selected partitions': the baseline records the
            // configuration as it stands, and ConnectedSystemScope.From is what narrows it to the partitions in
            // play. Container 20 sits in the deselected partition and is still recorded here.
            Assert.That(current.SelectedContainerIds, Is.EquivalentTo(new[] { 10, 12, 20 }));
        }
    }

    [Test]
    public void DescribesSameSelectionAs_SameSelectionButDifferentExclusions_ReturnsFalse()
    {
        // A preview an administrator is looking at stops answering their question when they change an exclusion,
        // exactly as when they change a selection.
        var withExclusion = new ConnectedSystemScopeSelectionProposal([1], [10], [11]);
        var withoutExclusion = new ConnectedSystemScopeSelectionProposal([1], [10]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withExclusion.DescribesSameSelectionAs(withoutExclusion), Is.False);
            Assert.That(withExclusion.DescribesSameSelectionAs(new ConnectedSystemScopeSelectionProposal([1], [10], [11])), Is.True);
        }
    }

    #endregion

    #region Helper Methods

    private static ConnectedSystemScope ScopeOver(
        ConnectedSystem connectedSystem,
        IReadOnlyList<int> selectedPartitionIds,
        IReadOnlyList<int> selectedContainerIds,
        IReadOnlyList<int>? excludedContainerIds = null) =>
        ConnectedSystemScope.From(
            connectedSystem,
            new ConnectedSystemScopeSelectionProposal(selectedPartitionIds, selectedContainerIds, excludedContainerIds),
            DistinguishedNameContainment.Instance);

    /// <summary>
    /// Two partitions: the first holding OU=Corp (id 10) with OU=Sales beneath it (id 11) and a sibling OU=Partners
    /// (id 12), the second holding OU=Archive (id 20).
    /// </summary>
    private static ConnectedSystem SystemWithContainers(
        ConnectedSystemContainerScope corpScope = ConnectedSystemContainerScope.Subtree)
    {
        var corp = Container(10, "OU=Corp,DC=example,DC=local", corpScope);
        var sales = Container(11, "OU=Sales,OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);
        corp.AddChildContainer(sales);

        return new ConnectedSystem
        {
            Name = "Test Directory",
            ConnectorDefinition = new ConnectorDefinition { Name = "Test Connector", SupportsPartitionContainers = true },
            Partitions =
            [
                new ConnectedSystemPartition
                {
                    Id = 1,
                    Name = "example.local",
                    ExternalId = "DC=example,DC=local",
                    Selected = true,
                    Containers = [corp, Container(12, "OU=Partners,DC=example,DC=local", ConnectedSystemContainerScope.Subtree)]
                },
                new ConnectedSystemPartition
                {
                    Id = 2,
                    Name = "archive.local",
                    ExternalId = "DC=archive,DC=local",
                    Selected = false,
                    Containers = [Container(20, "OU=Archive,DC=example,DC=local", ConnectedSystemContainerScope.Subtree)]
                }
            ]
        };
    }

    private static ConnectedSystemContainer Container(int id, string externalId, ConnectedSystemContainerScope scope) =>
        new() { Id = id, Name = externalId, ExternalId = externalId, Selected = true, Scope = scope };

    #endregion
}
