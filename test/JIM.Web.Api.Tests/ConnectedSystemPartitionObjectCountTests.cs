// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Every path that loads Containers has to rebuild their subtree object counts (#1276).
/// </summary>
/// <remarks>
/// <see cref="ConnectedSystemContainer.ObjectCount"/> is stored;
/// <see cref="ConnectedSystemContainer.SubtreeObjectCount"/> is derived, deliberately, so the two cannot disagree
/// once a Container moves. That makes "did this retrieval path rebuild it?" a question every new path has to answer,
/// and one nothing else asks: the figure is simply null, which renders as a blank rather than as an error.
///
/// This is a regression test for a real defect, found by driving the running portal rather than by any test. The
/// rebuild was wired into the full Connected System load, but the partitions endpoint and its PowerShell wrapper use
/// a different retrieval, so both reported every Subtree Container's own direct count and understated its branch by
/// everything beneath it.
/// </remarks>
[TestFixture]
public class ConnectedSystemPartitionObjectCountTests
{
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);

        _application = new JimApplication(mockRepository.Object);
    }

    [Test]
    public async Task GetConnectedSystemPartitionsAsync_ContainersFromTheDatabase_HaveTheirSubtreeTotalsRebuiltAsync()
    {
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Directory" };
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemPartitionsAsync(connectedSystem))
            .ReturnsAsync(() => new List<ConnectedSystemPartition> { PartitionWithStoredCounts() });

        var partitions = await _application.ConnectedSystems.GetConnectedSystemPartitionsAsync(connectedSystem);

        var people = FindContainer(partitions, "ou=People,dc=corp");
        var contractors = FindContainer(partitions, "ou=Contractors,ou=People,dc=corp");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(people.SubtreeObjectCount, Is.EqualTo(5), "its own two, plus the three beneath it");
            Assert.That(people.ObjectCount, Is.EqualTo(2), "the stored direct count is left alone");
            Assert.That(contractors.SubtreeObjectCount, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task GetConnectedSystemPartitionAsync_ASinglePartition_HasItsSubtreeTotalsRebuiltAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemPartitionAsync(7, false))
            .ReturnsAsync(PartitionWithStoredCounts);

        var partition = await _application.ConnectedSystems.GetConnectedSystemPartitionAsync(7);

        Assert.That(partition, Is.Not.Null);
        Assert.That(FindContainer([partition!], "ou=People,dc=corp").SubtreeObjectCount, Is.EqualTo(5));
    }

    [Test]
    public async Task GetConnectedSystemPartitionAsync_NoSuchPartition_ReturnsNullRatherThanThrowingAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemPartitionAsync(99, false))
            .ReturnsAsync((ConnectedSystemPartition?)null);

        Assert.That(await _application.ConnectedSystems.GetConnectedSystemPartitionAsync(99), Is.Null);
    }

    /// <summary>
    /// A partition as the database hands it back: direct counts stored, subtree totals absent because they are
    /// never stored.
    /// </summary>
    private static ConnectedSystemPartition PartitionWithStoredCounts()
    {
        var contractors = new ConnectedSystemContainer
        {
            ExternalId = "ou=Contractors,ou=People,dc=corp",
            Name = "Contractors",
            ObjectCount = 3
        };

        var people = new ConnectedSystemContainer
        {
            ExternalId = "ou=People,dc=corp",
            Name = "People",
            ObjectCount = 2
        };
        people.AddChildContainer(contractors);

        return new ConnectedSystemPartition
        {
            Id = 7,
            Name = "dc=corp",
            ExternalId = "dc=corp",
            Containers = [people]
        };
    }

    private static ConnectedSystemContainer FindContainer(IEnumerable<ConnectedSystemPartition> partitions, string externalId)
    {
        var found = partitions
            .SelectMany(partition => Flatten(partition.Containers ?? []))
            .FirstOrDefault(container => container.ExternalId == externalId);

        Assert.That(found, Is.Not.Null, $"no Container with external id {externalId}");
        return found!;
    }

    private static IEnumerable<ConnectedSystemContainer> Flatten(IEnumerable<ConnectedSystemContainer> containers)
    {
        foreach (var container in containers)
        {
            yield return container;

            foreach (var descendant in Flatten(container.ChildContainers))
                yield return descendant;
        }
    }
}
