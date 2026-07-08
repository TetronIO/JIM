// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Unit tests for the attribute contributor-count aggregate on
/// <see cref="JIM.Application.Servers.ConnectedSystemServer"/> (#91, Surface 2 multi-contributor badge): given all
/// import mappings targeting a Metaverse Object Type, group them by target Metaverse attribute into a per-attribute
/// contributor count. The repository is mocked, so these exercise the grouping in isolation without a database.
/// </summary>
[TestFixture]
public class AttributeContributorCountsTests
{
    private const int ObjectTypeId = 7;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);

        _jim = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    private static SyncRuleMapping BuildMapping(int id, int targetAttributeId)
    {
        return new SyncRuleMapping
        {
            Id = id,
            TargetMetaverseAttributeId = targetAttributeId,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = targetAttributeId, Name = $"attr{targetAttributeId}" },
            SyncRule = new SyncRule { Id = id * 100, Name = $"Rule {id}", Enabled = true }
        };
    }

    [Test]
    public async Task GetAttributeContributorCountsAsync_GroupsMappingsByTargetAttribute_ReturnsPerAttributeCountsAsync()
    {
        // Arrange: attribute 42 has three contributors, attribute 43 has one.
        _mockCsRepo
            .Setup(r => r.GetImportSyncRuleMappingsForMetaverseObjectTypeAsync(ObjectTypeId))
            .ReturnsAsync(new List<SyncRuleMapping>
            {
                BuildMapping(10, 42),
                BuildMapping(20, 42),
                BuildMapping(30, 42),
                BuildMapping(40, 43)
            });

        // Act
        var counts = await _jim.ConnectedSystems.GetAttributeContributorCountsAsync(ObjectTypeId);

        // Assert
        Assert.That(counts[42], Is.EqualTo(3));
        Assert.That(counts[43], Is.EqualTo(1));
    }

    [Test]
    public async Task GetAttributeContributorCountsAsync_NoContributors_ReturnsEmptyDictionaryAsync()
    {
        _mockCsRepo
            .Setup(r => r.GetImportSyncRuleMappingsForMetaverseObjectTypeAsync(ObjectTypeId))
            .ReturnsAsync(new List<SyncRuleMapping>());

        var counts = await _jim.ConnectedSystems.GetAttributeContributorCountsAsync(ObjectTypeId);

        Assert.That(counts, Is.Empty);
    }

    [Test]
    public async Task GetAttributeContributorCountsAsync_MappingWithNoTargetAttribute_IsIgnoredAsync()
    {
        // A malformed/export-shaped mapping with no target Metaverse attribute must not throw or count.
        var orphan = BuildMapping(50, 42);
        orphan.TargetMetaverseAttributeId = null;

        _mockCsRepo
            .Setup(r => r.GetImportSyncRuleMappingsForMetaverseObjectTypeAsync(ObjectTypeId))
            .ReturnsAsync(new List<SyncRuleMapping> { BuildMapping(10, 42), orphan });

        var counts = await _jim.ConnectedSystems.GetAttributeContributorCountsAsync(ObjectTypeId);

        Assert.That(counts[42], Is.EqualTo(1));
        Assert.That(counts.Values.Sum(), Is.EqualTo(1));
    }
}
