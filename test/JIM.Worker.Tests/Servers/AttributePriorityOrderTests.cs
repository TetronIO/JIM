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
/// Unit tests for the attribute priority order logic on
/// <see cref="JIM.Application.Servers.ConnectedSystemServer"/> (#91): validating a requested order against the
/// attribute's current contributors and renumbering sibling <see cref="SyncRuleMapping.Priority"/> rows.
/// The repository is mocked, so these exercise the validation and renumbering rules in isolation without a database.
/// </summary>
[TestFixture]
public class AttributePriorityOrderTests
{
    private const int ObjectTypeId = 7;
    private const int AttributeId = 42;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _user = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();

        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);

        // Activity persistence is a no-op for these tests (we assert on the renumbering, not the audit trail).
        _mockActivityRepo
            .Setup(r => r.CreateActivityAsync(It.IsAny<JIM.Models.Activities.Activity>()))
            .Returns(Task.CompletedTask);
        _mockActivityRepo
            .Setup(r => r.UpdateActivityAsync(It.IsAny<JIM.Models.Activities.Activity>()))
            .Returns(Task.CompletedTask);

        _mockCsRepo
            .Setup(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>()))
            .Returns(Task.CompletedTask);

        _jim = new JimApplication(_mockRepository.Object);

        _user = new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = "Test Admin" };
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    private static SyncRuleMapping BuildMapping(int id, int priority, bool nullIsValue)
    {
        return new SyncRuleMapping
        {
            Id = id,
            Priority = priority,
            NullIsValue = nullIsValue,
            TargetMetaverseAttributeId = AttributeId,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = AttributeId, Name = "department" },
            SyncRule = new SyncRule
            {
                Id = id * 100,
                Name = $"Rule {id}",
                Enabled = true,
                ConnectedSystem = new ConnectedSystem { Id = id, Name = $"System {id}" }
            }
        };
    }

    private void SetupContributors(params SyncRuleMapping[] mappings)
    {
        _mockCsRepo
            .Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(ObjectTypeId, AttributeId))
            .ReturnsAsync(mappings.ToList());
    }

    [Test]
    public async Task SetAttributePriorityOrderAsync_ValidCompleteReorder_RenumbersPrioritiesAndAppliesNullIsValueAsync()
    {
        // Arrange: three contributors currently ordered 10, 20, 30.
        var m10 = BuildMapping(10, priority: 1, nullIsValue: true);
        var m20 = BuildMapping(20, priority: 2, nullIsValue: false);
        var m30 = BuildMapping(30, priority: 3, nullIsValue: false);
        SetupContributors(m10, m20, m30);

        // Desired new order: 20 (highest), 30, 10. Flip NullIsValue onto 30, off 10.
        var requested = new List<(int MappingId, bool NullIsValue)>
        {
            (20, false),
            (30, true),
            (10, false)
        };

        // Act
        await _jim.ConnectedSystems.SetAttributePriorityOrderAsync(ObjectTypeId, AttributeId, requested, _user);

        // Assert: priorities renumbered 1..N in the requested order.
        Assert.That(m20.Priority, Is.EqualTo(1));
        Assert.That(m30.Priority, Is.EqualTo(2));
        Assert.That(m10.Priority, Is.EqualTo(3));

        // NullIsValue applied per the request.
        Assert.That(m20.NullIsValue, Is.False);
        Assert.That(m30.NullIsValue, Is.True);
        Assert.That(m10.NullIsValue, Is.False);

        // Persisted once, transactionally, with all three mappings.
        _mockCsRepo.Verify(r => r.UpdateSyncRuleMappingsAsync(
            It.Is<IReadOnlyCollection<SyncRuleMapping>>(c => c.Count == 3)), Times.Once);
    }

    [Test]
    public void SetAttributePriorityOrderAsync_DuplicateMappingIds_ThrowsAndDoesNotPersist()
    {
        var m10 = BuildMapping(10, 1, false);
        var m20 = BuildMapping(20, 2, false);
        var m30 = BuildMapping(30, 3, false);
        SetupContributors(m10, m20, m30);

        var requested = new List<(int MappingId, bool NullIsValue)>
        {
            (10, false),
            (10, false),
            (20, false)
        };

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _jim.ConnectedSystems.SetAttributePriorityOrderAsync(ObjectTypeId, AttributeId, requested, _user));

        _mockCsRepo.Verify(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>()), Times.Never);
    }

    [Test]
    public void SetAttributePriorityOrderAsync_MissingContributor_ThrowsAndDoesNotPersist()
    {
        var m10 = BuildMapping(10, 1, false);
        var m20 = BuildMapping(20, 2, false);
        var m30 = BuildMapping(30, 3, false);
        SetupContributors(m10, m20, m30);

        // 30 omitted: a partial reorder must be rejected (would leave a gap / stale priority).
        var requested = new List<(int MappingId, bool NullIsValue)>
        {
            (10, false),
            (20, false)
        };

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _jim.ConnectedSystems.SetAttributePriorityOrderAsync(ObjectTypeId, AttributeId, requested, _user));

        _mockCsRepo.Verify(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>()), Times.Never);
    }

    [Test]
    public void SetAttributePriorityOrderAsync_UnknownContributor_ThrowsAndDoesNotPersist()
    {
        var m10 = BuildMapping(10, 1, false);
        var m20 = BuildMapping(20, 2, false);
        SetupContributors(m10, m20);

        // 99 is not a contributor to this attribute: an out-of-set mapping must be rejected.
        var requested = new List<(int MappingId, bool NullIsValue)>
        {
            (10, false),
            (20, false),
            (99, false)
        };

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _jim.ConnectedSystems.SetAttributePriorityOrderAsync(ObjectTypeId, AttributeId, requested, _user));

        _mockCsRepo.Verify(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>()), Times.Never);
    }

    [Test]
    public void SetAttributePriorityOrderAsync_NoContributors_ThrowsAndDoesNotPersist()
    {
        SetupContributors();

        var requested = new List<(int MappingId, bool NullIsValue)> { (10, false) };

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _jim.ConnectedSystems.SetAttributePriorityOrderAsync(ObjectTypeId, AttributeId, requested, _user));

        _mockCsRepo.Verify(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>()), Times.Never);
    }

    [Test]
    public async Task GetAttributePriorityOrderAsync_DelegatesToRepositoryAndReturnsOrderedListAsync()
    {
        var m10 = BuildMapping(10, 1, false);
        var m20 = BuildMapping(20, 2, false);
        SetupContributors(m10, m20);

        var result = await _jim.ConnectedSystems.GetAttributePriorityOrderAsync(ObjectTypeId, AttributeId);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0].Id, Is.EqualTo(10));
        Assert.That(result[1].Id, Is.EqualTo(20));
        _mockCsRepo.Verify(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(ObjectTypeId, AttributeId), Times.Once);
    }

    [Test]
    public async Task MoveAttributePriorityAsync_MoveThirdToSecond_ShufflesPreviousSecondDownAsync()
    {
        // Current order: 10 (1), 20 (2), 30 (3). The user's scenario: move the third to position 2.
        var m10 = BuildMapping(10, 1, false);
        var m20 = BuildMapping(20, 2, false);
        var m30 = BuildMapping(30, 3, false);
        SetupContributors(m10, m20, m30);

        var result = await _jim.ConnectedSystems.MoveAttributePriorityAsync(ObjectTypeId, AttributeId, mappingId: 30, targetPosition: 2, nullIsValue: null, _user);

        // Expected: 10 stays at 1, 30 takes position 2, the previous second (20) shuffles down to 3.
        Assert.That(m10.Priority, Is.EqualTo(1));
        Assert.That(m30.Priority, Is.EqualTo(2));
        Assert.That(m20.Priority, Is.EqualTo(3));

        // Returned order reflects the new arrangement, highest priority first.
        Assert.That(result.Select(m => m.Id), Is.EqualTo(new[] { 10, 30, 20 }));

        // Only the rows that actually changed are persisted (10 did not move).
        _mockCsRepo.Verify(r => r.UpdateSyncRuleMappingsAsync(
            It.Is<IReadOnlyCollection<SyncRuleMapping>>(c => c.Count == 2 && c.All(m => m.Id != 10))), Times.Once);
    }

    [Test]
    public async Task MoveAttributePriorityAsync_WithNullIsValueOverride_AppliesItToTheMovedMappingAsync()
    {
        var m10 = BuildMapping(10, 1, false);
        var m20 = BuildMapping(20, 2, false);
        SetupContributors(m10, m20);

        await _jim.ConnectedSystems.MoveAttributePriorityAsync(ObjectTypeId, AttributeId, mappingId: 20, targetPosition: 1, nullIsValue: true, _user);

        Assert.That(m20.Priority, Is.EqualTo(1));
        Assert.That(m10.Priority, Is.EqualTo(2));
        Assert.That(m20.NullIsValue, Is.True);
    }

    [Test]
    public async Task MoveAttributePriorityAsync_PositionBeyondEnd_ClampsToLastAsync()
    {
        var m10 = BuildMapping(10, 1, false);
        var m20 = BuildMapping(20, 2, false);
        var m30 = BuildMapping(30, 3, false);
        SetupContributors(m10, m20, m30);

        // Position 99 is out of range; it should clamp to the last position.
        var result = await _jim.ConnectedSystems.MoveAttributePriorityAsync(ObjectTypeId, AttributeId, mappingId: 10, targetPosition: 99, nullIsValue: null, _user);

        Assert.That(result.Select(m => m.Id), Is.EqualTo(new[] { 20, 30, 10 }));
        Assert.That(m10.Priority, Is.EqualTo(3));
    }

    [Test]
    public async Task MoveAttributePriorityAsync_NoOpMove_PersistsNothingAsync()
    {
        var m10 = BuildMapping(10, 1, false);
        var m20 = BuildMapping(20, 2, false);
        SetupContributors(m10, m20);

        // Moving the first mapping to position 1 changes nothing.
        var result = await _jim.ConnectedSystems.MoveAttributePriorityAsync(ObjectTypeId, AttributeId, mappingId: 10, targetPosition: 1, nullIsValue: null, _user);

        Assert.That(result.Select(m => m.Id), Is.EqualTo(new[] { 10, 20 }));
        _mockCsRepo.Verify(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>()), Times.Never);
    }

    [Test]
    public void MoveAttributePriorityAsync_UnknownMapping_ThrowsAndDoesNotPersist()
    {
        var m10 = BuildMapping(10, 1, false);
        var m20 = BuildMapping(20, 2, false);
        SetupContributors(m10, m20);

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _jim.ConnectedSystems.MoveAttributePriorityAsync(ObjectTypeId, AttributeId, mappingId: 99, targetPosition: 1, nullIsValue: null, _user));

        _mockCsRepo.Verify(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>()), Times.Never);
    }

    [Test]
    public void MoveAttributePriorityAsync_NoContributors_ThrowsAndDoesNotPersist()
    {
        SetupContributors();

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _jim.ConnectedSystems.MoveAttributePriorityAsync(ObjectTypeId, AttributeId, mappingId: 10, targetPosition: 1, nullIsValue: null, _user));

        _mockCsRepo.Verify(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>()), Times.Never);
    }
}
