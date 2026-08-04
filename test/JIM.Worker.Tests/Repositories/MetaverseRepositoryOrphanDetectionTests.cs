// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests for the orphan detection query logic in MetaverseRepository.
/// These tests verify that GetMvosOrphanedByConnectedSystemDeletionAsync correctly identifies:
/// - MVOs that will become orphaned when a Connected System is deleted
/// - MVOs that should NOT be orphaned (multiple connectors, internal origin, manual deletion rule)
/// </summary>
[TestFixture]
public class MetaverseRepositoryOrphanDetectionTests
{
    private Mock<JimDbContext> _mockDbContext = null!;
    private List<MetaverseObject> _metaverseObjectsData = null!;
    private Mock<DbSet<MetaverseObject>> _mockDbSetMetaverseObjects = null!;
    private PostgresDataRepository _repository = null!;

    // Test Connected System IDs
    private const int HrSystemId = 1;
    private const int AdSystemId = 2;
    // A system that is never a listed authoritative source (a provisioning target).
    private const int TargetSystemId = 3;

    // Test MVO Type with WhenLastConnectorDisconnected deletion rule
    private MetaverseObjectType _personTypeWithDeletionRule = null!;
    // Test MVO Type with Manual deletion rule
    private MetaverseObjectType _personTypeWithManualDeletion = null!;
    // Test MVO Types with WhenAuthoritativeSourceDisconnected deletion rule, one per trigger mode (#119)
    private MetaverseObjectType _personTypeAuthoritativeSpecific = null!;
    private MetaverseObjectType _personTypeAuthoritativeAll = null!;

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        // Create MVO types
        _personTypeWithDeletionRule = new MetaverseObjectType
        {
            Id = 1,
            Name = "Person",
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.FromDays(30)
        };

        _personTypeWithManualDeletion = new MetaverseObjectType
        {
            Id = 2,
            Name = "ServiceAccount",
            DeletionRule = MetaverseObjectDeletionRule.Manual
        };

        _personTypeAuthoritativeSpecific = new MetaverseObjectType
        {
            Id = 3,
            Name = "PersonSpecificSources",
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            DeletionTriggerConnectedSystemIds = new List<int> { HrSystemId, AdSystemId },
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            DeletionGracePeriod = TimeSpan.FromDays(30)
        };

        _personTypeAuthoritativeAll = new MetaverseObjectType
        {
            Id = 4,
            Name = "PersonAllSources",
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            DeletionTriggerConnectedSystemIds = new List<int> { HrSystemId, AdSystemId },
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            DeletionGracePeriod = TimeSpan.FromDays(30)
        };

        // Initialise empty data - tests will populate as needed
        _metaverseObjectsData = new List<MetaverseObject>();
    }

    private void SetupMockDbContext()
    {
        _mockDbSetMetaverseObjects = _metaverseObjectsData.BuildMockDbSet();
        _mockDbContext = new Mock<JimDbContext>();
        _mockDbContext.Setup(m => m.MetaverseObjects).Returns(_mockDbSetMetaverseObjects.Object);
        _repository = new PostgresDataRepository(_mockDbContext.Object);
    }

    #region Orphan Detection Tests

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithMvoOnlyInDeletedSystem_ReturnsAsOrphanAsync()
    {
        // Arrange - MVO with CSO only in the HR system (being deleted)
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - should be orphaned
        Assert.That(orphanedMvos, Has.Count.EqualTo(1));
        Assert.That(orphanedMvos[0].Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithMvoInMultipleSystems_DoesNotReturnAsOrphanAsync()
    {
        // Arrange - MVO with CSOs in both HR (being deleted) and AD (remaining)
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        mvo.ConnectedSystemObjects.Add(CreateCso(AdSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - should NOT be orphaned (has connector in AD)
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithInternalOrigin_DoesNotReturnAsOrphanAsync()
    {
        // Arrange - Internal MVO (like admin accounts) with CSO only in deleted system
        var mvo = CreateInternalMvo(_personTypeWithDeletionRule);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - should NOT be orphaned (internal origin is protected)
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithManualDeletionRule_DoesNotReturnAsOrphanAsync()
    {
        // Arrange - MVO with Manual deletion rule (only applies to WhenLastConnectorDisconnected)
        var mvo = CreateProjectedMvo(_personTypeWithManualDeletion);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - should NOT be orphaned (Manual deletion rule)
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithNoCsoInDeletedSystem_DoesNotReturnAsOrphanAsync()
    {
        // Arrange - MVO with CSO only in AD (not the system being deleted)
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo.ConnectedSystemObjects.Add(CreateCso(AdSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - should NOT be orphaned (no CSO in the deleted system)
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithMultipleOrphanedMvos_ReturnsAllOrphansAsync()
    {
        // Arrange - Three MVOs with CSOs only in HR system
        var mvo1 = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo1.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo1));

        var mvo2 = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo2.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo2));

        var mvo3 = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo3.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo3));

        _metaverseObjectsData.AddRange(new[] { mvo1, mvo2, mvo3 });
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - all three should be orphaned
        Assert.That(orphanedMvos, Has.Count.EqualTo(3));
        Assert.That(orphanedMvos.Select(m => m.Id), Is.EquivalentTo(new[] { mvo1.Id, mvo2.Id, mvo3.Id }));
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithMixedScenarios_ReturnsOnlyOrphansAsync()
    {
        // Arrange - Mix of orphaned and non-orphaned MVOs

        // Should be orphaned: Projected MVO with CSO only in HR
        var orphanedMvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        orphanedMvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, orphanedMvo));

        // Should NOT be orphaned: MVO with CSOs in both systems
        var multiConnectorMvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        multiConnectorMvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, multiConnectorMvo));
        multiConnectorMvo.ConnectedSystemObjects.Add(CreateCso(AdSystemId, multiConnectorMvo));

        // Should NOT be orphaned: Internal origin
        var internalMvo = CreateInternalMvo(_personTypeWithDeletionRule);
        internalMvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, internalMvo));

        // Should NOT be orphaned: Manual deletion rule
        var manualDeletionMvo = CreateProjectedMvo(_personTypeWithManualDeletion);
        manualDeletionMvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, manualDeletionMvo));

        // Should NOT be orphaned: No CSO in deleted system
        var otherSystemMvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        otherSystemMvo.ConnectedSystemObjects.Add(CreateCso(AdSystemId, otherSystemMvo));

        _metaverseObjectsData.AddRange(new[] { orphanedMvo, multiConnectorMvo, internalMvo, manualDeletionMvo, otherSystemMvo });
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - only the first MVO should be orphaned
        Assert.That(orphanedMvos, Has.Count.EqualTo(1));
        Assert.That(orphanedMvos[0].Id, Is.EqualTo(orphanedMvo.Id));
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithMvoWithMultipleCsosInSameSystem_ReturnsAsOrphanAsync()
    {
        // Arrange - MVO with multiple CSOs but all in the same system being deleted
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo)); // First CSO in HR
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo)); // Second CSO in HR (different partition perhaps)
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - should be orphaned (all CSOs are in the deleted system)
        Assert.That(orphanedMvos, Has.Count.EqualTo(1));
        Assert.That(orphanedMvos[0].Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithNoMvos_ReturnsEmptyListAsync()
    {
        // Arrange - no MVOs in the system
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithMvoWithNoCsos_DoesNotReturnAsOrphanAsync()
    {
        // Arrange - MVO with no CSOs at all
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        // No CSOs added
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - should NOT be returned (no CSO in the deleted system)
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithAlreadyMarkedMvo_DoesNotReturnAsync()
    {
        // Arrange - MVO already pending deletion (marked by an earlier disconnection); re-marking would
        // overwrite the original decision's trigger fields and snapshot, so it must not be returned.
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-1);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - already marked, nothing further to do
        Assert.That(orphanedMvos, Is.Empty);
    }

    #endregion

    #region Authoritative Source Trigger Mode Tests (#119)

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_SpecificModeWithOtherListedSourceStillConnected_ReturnsAsOrphanAsync()
    {
        // Arrange - Specific mode: any listed source disconnecting triggers deletion, even though the
        // other listed source (AD) remains connected.
        var mvo = CreateProjectedMvo(_personTypeAuthoritativeSpecific);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        mvo.ConnectedSystemObjects.Add(CreateCso(AdSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert
        Assert.That(orphanedMvos, Has.Count.EqualTo(1));
        Assert.That(orphanedMvos[0].Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_SpecificModeWithUnlistedSystemDeleted_DoesNotReturnAsync()
    {
        // Arrange - the system being deleted is not a listed source, so it never triggers deletion.
        var mvo = CreateProjectedMvo(_personTypeAuthoritativeSpecific);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        mvo.ConnectedSystemObjects.Add(CreateCso(TargetSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(TargetSystemId);

        // Assert
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_AllModeWithOtherListedSourceStillConnected_DoesNotReturnAsync()
    {
        // Arrange - All mode: deleting one of two still-connected listed sources must not mark the MVO,
        // because the other listed source (AD) still holds a joined CSO.
        var mvo = CreateProjectedMvo(_personTypeAuthoritativeAll);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        mvo.ConnectedSystemObjects.Add(CreateCso(AdSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_AllModeWithLastListedSourceDeleted_ReturnsAsOrphanAsync()
    {
        // Arrange - All mode: the deleted system is the last listed source holding a joined CSO.
        var mvo = CreateProjectedMvo(_personTypeAuthoritativeAll);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert
        Assert.That(orphanedMvos, Has.Count.EqualTo(1));
        Assert.That(orphanedMvos[0].Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_AllModeWithOnlyUnlistedTargetRemaining_ReturnsAsOrphanAsync()
    {
        // Arrange - All mode: a remaining connection to an unlisted system (a provisioning target) does
        // not block deletion; only listed sources do.
        var mvo = CreateProjectedMvo(_personTypeAuthoritativeAll);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        mvo.ConnectedSystemObjects.Add(CreateCso(TargetSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert
        Assert.That(orphanedMvos, Has.Count.EqualTo(1));
        Assert.That(orphanedMvos[0].Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_AllModeWithMvoOnlyInUnlistedSystem_DoesNotReturnAsync()
    {
        // Arrange - MVO connected only to an unlisted system; deleting a listed source it has no
        // connection to must leave it unaffected.
        var mvo = CreateProjectedMvo(_personTypeAuthoritativeAll);
        mvo.ConnectedSystemObjects.Add(CreateCso(TargetSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_AllModeWithUnlistedSystemDeleted_DoesNotReturnAsync()
    {
        // Arrange - deleting an unlisted system never triggers the rule, regardless of mode.
        var mvo = CreateProjectedMvo(_personTypeAuthoritativeAll);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        mvo.ConnectedSystemObjects.Add(CreateCso(TargetSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(TargetSystemId);

        // Assert
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_AuthoritativeSourceWithEmptyTriggerList_FallsBackToLastConnectorSemanticsAsync()
    {
        // Arrange - no sources configured: the engine falls back to WhenLastConnectorDisconnected
        // semantics, so only the MVO with no other connections is orphaned.
        var emptyListType = new MetaverseObjectType
        {
            Id = 5,
            Name = "PersonNoSources",
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            DeletionTriggerConnectedSystemIds = new List<int>(),
            DeletionGracePeriod = TimeSpan.FromDays(30)
        };

        var orphanedMvo = CreateProjectedMvo(emptyListType);
        orphanedMvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, orphanedMvo));

        var stillConnectedMvo = CreateProjectedMvo(emptyListType);
        stillConnectedMvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, stillConnectedMvo));
        stillConnectedMvo.ConnectedSystemObjects.Add(CreateCso(AdSystemId, stillConnectedMvo));

        _metaverseObjectsData.AddRange(new[] { orphanedMvo, stillConnectedMvo });
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert
        Assert.That(orphanedMvos, Has.Count.EqualTo(1));
        Assert.That(orphanedMvos[0].Id, Is.EqualTo(orphanedMvo.Id));
    }

    #endregion

    #region Preview Count Agreement Tests (#119)

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionCountAsync_SpecificModeMixedPopulation_MatchesListCountAsync()
    {
        // Arrange - mixed population under Specific mode; the count query must agree exactly with the
        // list query because the deletion preview renders one and execution marks the other.
        var triggeredMvo = CreateProjectedMvo(_personTypeAuthoritativeSpecific);
        triggeredMvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, triggeredMvo));
        triggeredMvo.ConnectedSystemObjects.Add(CreateCso(AdSystemId, triggeredMvo));

        var unaffectedMvo = CreateProjectedMvo(_personTypeAuthoritativeSpecific);
        unaffectedMvo.ConnectedSystemObjects.Add(CreateCso(AdSystemId, unaffectedMvo));

        var lastConnectorMvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        lastConnectorMvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, lastConnectorMvo));

        _metaverseObjectsData.AddRange(new[] { triggeredMvo, unaffectedMvo, lastConnectorMvo });
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);
        var orphanedCount = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionCountAsync(HrSystemId);

        // Assert
        Assert.That(orphanedMvos, Has.Count.EqualTo(2));
        Assert.That(orphanedCount, Is.EqualTo(orphanedMvos.Count));
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionCountAsync_AllModeMixedPopulation_MatchesListCountAsync()
    {
        // Arrange - mixed population under All mode: one MVO blocked by a remaining listed source, one
        // markable because only an unlisted target remains.
        var blockedMvo = CreateProjectedMvo(_personTypeAuthoritativeAll);
        blockedMvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, blockedMvo));
        blockedMvo.ConnectedSystemObjects.Add(CreateCso(AdSystemId, blockedMvo));

        var markableMvo = CreateProjectedMvo(_personTypeAuthoritativeAll);
        markableMvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, markableMvo));
        markableMvo.ConnectedSystemObjects.Add(CreateCso(TargetSystemId, markableMvo));

        _metaverseObjectsData.AddRange(new[] { blockedMvo, markableMvo });
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);
        var orphanedCount = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionCountAsync(HrSystemId);

        // Assert
        Assert.That(orphanedMvos, Has.Count.EqualTo(1));
        Assert.That(orphanedMvos[0].Id, Is.EqualTo(markableMvo.Id));
        Assert.That(orphanedCount, Is.EqualTo(orphanedMvos.Count));
    }

    #endregion

    #region Helper Methods

    private static MetaverseObject CreateProjectedMvo(MetaverseObjectType type)
    {
        return new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Origin = MetaverseObjectOrigin.Projected,
            Type = type,
            ConnectedSystemObjects = new List<ConnectedSystemObject>()
        };
    }

    private static MetaverseObject CreateInternalMvo(MetaverseObjectType type)
    {
        return new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Origin = MetaverseObjectOrigin.Internal,
            Type = type,
            ConnectedSystemObjects = new List<ConnectedSystemObject>()
        };
    }

    private static ConnectedSystemObject CreateCso(int connectedSystemId, MetaverseObject mvo)
    {
        return new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            DateJoined = DateTime.UtcNow
        };
    }

    #endregion
}
