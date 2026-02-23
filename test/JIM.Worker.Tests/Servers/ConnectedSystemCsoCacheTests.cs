using JIM.Application;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

[TestFixture]
public class ConnectedSystemCsoCacheTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private IMemoryCache _cache = null!;
    private JimApplication _jim = null!;

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        _cache?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);

        _cache = new MemoryCache(new MemoryCacheOptions());
        _jim = new JimApplication(_mockRepository.Object, _cache);
    }

    #region BuildCsoCacheKey Tests

    [Test]
    public void BuildCsoCacheKey_ReturnsExpectedFormat()
    {
        var key = ConnectedSystemServer.BuildCsoCacheKey(1, 42, "cn=john");
        Assert.That(key, Is.EqualTo("cso:1:42:cn=john"));
    }

    [Test]
    public void BuildCsoCacheKey_DifferentParameters_ReturnsDifferentKeys()
    {
        var key1 = ConnectedSystemServer.BuildCsoCacheKey(1, 42, "cn=john");
        var key2 = ConnectedSystemServer.BuildCsoCacheKey(2, 42, "cn=john");
        var key3 = ConnectedSystemServer.BuildCsoCacheKey(1, 43, "cn=john");

        Assert.That(key1, Is.Not.EqualTo(key2));
        Assert.That(key1, Is.Not.EqualTo(key3));
    }

    #endregion

    #region Cache Hit Tests

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_CacheHit_LoadsByPrimaryKeyAsync()
    {
        // Arrange
        var connectedSystemId = 1;
        var attributeId = 42;
        var attributeValue = "cn=john.smith";
        var csoId = Guid.NewGuid();
        var expectedCso = new ConnectedSystemObject { Id = csoId };

        // Pre-populate cache
        var cacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, attributeId, attributeValue.ToLowerInvariant());
        _cache.Set(cacheKey, csoId);

        // Mock PK lookup
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectAsync(connectedSystemId, csoId))
            .ReturnsAsync(expectedCso);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(
            connectedSystemId, attributeId, attributeValue);

        // Assert
        Assert.That(result, Is.EqualTo(expectedCso));
        // Should NOT have called the attribute-based lookup
        _mockCsRepo.Verify(
            r => r.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue),
            Times.Never);
        // Should have called the PK-based lookup
        _mockCsRepo.Verify(
            r => r.GetConnectedSystemObjectAsync(connectedSystemId, csoId),
            Times.Once);
    }

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_CacheHit_CsoDeleted_EvictsAndFallsBackToDbAsync()
    {
        // Arrange
        var connectedSystemId = 1;
        var attributeId = 42;
        var attributeValue = "cn=deleted.user";
        var staleCsoId = Guid.NewGuid();

        // Pre-populate cache with a stale entry
        var cacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, attributeId, attributeValue.ToLowerInvariant());
        _cache.Set(cacheKey, staleCsoId);

        // PK lookup returns null (CSO was deleted)
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectAsync(connectedSystemId, staleCsoId))
            .ReturnsAsync((ConnectedSystemObject?)null);

        // Fallback attribute lookup also returns null
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue))
            .ReturnsAsync((ConnectedSystemObject?)null);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(
            connectedSystemId, attributeId, attributeValue);

        // Assert
        Assert.That(result, Is.Null);
        // Stale cache entry should be evicted
        Assert.That(_cache.TryGetValue(cacheKey, out Guid _), Is.False);
    }

    #endregion

    #region Cache Miss Tests

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_CacheMiss_QueriesDbAndPopulatesCacheAsync()
    {
        // Arrange
        var connectedSystemId = 1;
        var attributeId = 42;
        var attributeValue = "cn=new.user";
        var csoId = Guid.NewGuid();
        var cso = new ConnectedSystemObject { Id = csoId };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue))
            .ReturnsAsync(cso);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(
            connectedSystemId, attributeId, attributeValue);

        // Assert
        Assert.That(result, Is.EqualTo(cso));

        // Cache should now contain the entry
        var cacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, attributeId, attributeValue.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(cacheKey, out Guid cachedId), Is.True);
        Assert.That(cachedId, Is.EqualTo(csoId));
    }

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_CacheMiss_NullResult_DoesNotPopulateCacheAsync()
    {
        // Arrange
        var connectedSystemId = 1;
        var attributeId = 42;
        var attributeValue = "cn=nonexistent";

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue))
            .ReturnsAsync((ConnectedSystemObject?)null);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(
            connectedSystemId, attributeId, attributeValue);

        // Assert
        Assert.That(result, Is.Null);

        // Cache should NOT contain an entry for a null result
        var cacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, attributeId, attributeValue.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(cacheKey, out Guid _), Is.False);
    }

    #endregion

    #region Cache Miss (No Cache) Tests

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_NoCache_FallsBackToDirectDbQueryAsync()
    {
        // Arrange — create JimApplication without cache
        using var jimNoCache = new JimApplication(_mockRepository.Object);
        var connectedSystemId = 1;
        var attributeId = 42;
        var attributeValue = "cn=no.cache";
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue))
            .ReturnsAsync(cso);

        // Act
        var result = await jimNoCache.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(
            connectedSystemId, attributeId, attributeValue);

        // Assert
        Assert.That(result, Is.EqualTo(cso));
        _mockCsRepo.Verify(
            r => r.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue),
            Times.Once);
    }

    #endregion

    #region Typed Overload Tests

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_IntOverload_UsesCacheAsync()
    {
        // Arrange
        var connectedSystemId = 1;
        var attributeId = 42;
        var attributeValue = 12345;
        var csoId = Guid.NewGuid();
        var cso = new ConnectedSystemObject { Id = csoId };

        // Pre-populate cache
        var cacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, attributeId, attributeValue.ToString());
        _cache.Set(cacheKey, csoId);

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectAsync(connectedSystemId, csoId))
            .ReturnsAsync(cso);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(
            connectedSystemId, attributeId, attributeValue);

        // Assert
        Assert.That(result, Is.EqualTo(cso));
        _mockCsRepo.Verify(
            r => r.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue),
            Times.Never);
    }

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_GuidOverload_UsesCacheAsync()
    {
        // Arrange
        var connectedSystemId = 1;
        var attributeId = 42;
        var attributeValue = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        var csoId = Guid.NewGuid();
        var cso = new ConnectedSystemObject { Id = csoId };

        // Pre-populate cache (Guid external IDs are lowercased)
        var cacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, attributeId, attributeValue.ToString().ToLowerInvariant());
        _cache.Set(cacheKey, csoId);

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectAsync(connectedSystemId, csoId))
            .ReturnsAsync(cso);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(
            connectedSystemId, attributeId, attributeValue);

        // Assert
        Assert.That(result, Is.EqualTo(cso));
        _mockCsRepo.Verify(
            r => r.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue),
            Times.Never);
    }

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_LongOverload_UsesCacheAsync()
    {
        // Arrange
        var connectedSystemId = 1;
        var attributeId = 42;
        var attributeValue = 9876543210L;
        var csoId = Guid.NewGuid();
        var cso = new ConnectedSystemObject { Id = csoId };

        // Pre-populate cache
        var cacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, attributeId, attributeValue.ToString());
        _cache.Set(cacheKey, csoId);

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectAsync(connectedSystemId, csoId))
            .ReturnsAsync(cso);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(
            connectedSystemId, attributeId, attributeValue);

        // Assert
        Assert.That(result, Is.EqualTo(cso));
        _mockCsRepo.Verify(
            r => r.GetConnectedSystemObjectByAttributeAsync(connectedSystemId, attributeId, attributeValue),
            Times.Never);
    }

    #endregion

    #region AddCsoToCache / EvictCsoFromCache Tests

    [Test]
    public void AddCsoToCache_AddsEntryToCache()
    {
        // Arrange
        var connectedSystemId = 1;
        var attributeId = 42;
        var externalIdValue = "CN=Test User";
        var csoId = Guid.NewGuid();

        // Act
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, attributeId, externalIdValue, csoId);

        // Assert
        var cacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, attributeId, externalIdValue.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(cacheKey, out Guid cachedId), Is.True);
        Assert.That(cachedId, Is.EqualTo(csoId));
    }

    [Test]
    public void AddCsoToCache_CaseInsensitive_SameKeyRegardlessOfCase()
    {
        // Arrange
        var connectedSystemId = 1;
        var attributeId = 42;
        var csoId = Guid.NewGuid();

        // Act
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, attributeId, "CN=Test User", csoId);

        // Assert — should be findable with lowercase key
        var cacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, attributeId, "cn=test user");
        Assert.That(_cache.TryGetValue(cacheKey, out Guid cachedId), Is.True);
        Assert.That(cachedId, Is.EqualTo(csoId));
    }

    [Test]
    public void EvictCsoFromCache_RemovesEntryFromCache()
    {
        // Arrange
        var connectedSystemId = 1;
        var attributeId = 42;
        var externalIdValue = "cn=test";
        var csoId = Guid.NewGuid();

        // Pre-populate
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, attributeId, externalIdValue, csoId);

        // Act
        _jim.ConnectedSystems.EvictCsoFromCache(connectedSystemId, attributeId, externalIdValue);

        // Assert
        var cacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, attributeId, externalIdValue.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(cacheKey, out Guid _), Is.False);
    }

    [Test]
    public void AddCsoToCache_Upsert_OverwritesExistingEntry()
    {
        // Arrange
        var connectedSystemId = 1;
        var attributeId = 42;
        var externalIdValue = "cn=test";
        var oldCsoId = Guid.NewGuid();
        var newCsoId = Guid.NewGuid();

        // Pre-populate with old value
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, attributeId, externalIdValue, oldCsoId);

        // Act — overwrite with new value (simulates Set() upsert)
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, attributeId, externalIdValue, newCsoId);

        // Assert
        var cacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, attributeId, externalIdValue.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(cacheKey, out Guid cachedId), Is.True);
        Assert.That(cachedId, Is.EqualTo(newCsoId));
    }

    #endregion

    #region WarmCsoCacheAsync Tests

    [Test]
    public async Task WarmCsoCacheAsync_PopulatesAllMappingsAsync()
    {
        // Arrange
        var connectedSystemId = 1;
        var mappings = new Dictionary<string, Guid>
        {
            { "cso:1:42:cn=user1", Guid.NewGuid() },
            { "cso:1:42:cn=user2", Guid.NewGuid() },
            { "cso:1:42:cn=user3", Guid.NewGuid() }
        };

        _mockCsRepo.Setup(r => r.GetAllCsoExternalIdMappingsAsync(connectedSystemId))
            .ReturnsAsync(mappings);

        // Act
        await _jim.ConnectedSystems.WarmCsoCacheAsync(connectedSystemId);

        // Assert
        foreach (var mapping in mappings)
        {
            Assert.That(_cache.TryGetValue(mapping.Key, out Guid cachedId), Is.True);
            Assert.That(cachedId, Is.EqualTo(mapping.Value));
        }
    }

    [Test]
    public async Task WarmCsoCacheAsync_EmptyConnectedSystem_DoesNotFailAsync()
    {
        // Arrange
        var connectedSystemId = 1;
        _mockCsRepo.Setup(r => r.GetAllCsoExternalIdMappingsAsync(connectedSystemId))
            .ReturnsAsync(new Dictionary<string, Guid>());

        // Act & Assert — should not throw
        await _jim.ConnectedSystems.WarmCsoCacheAsync(connectedSystemId);
    }

    #endregion

    #region No Cache (JIM.Web) Tests

    [Test]
    public void AddCsoToCache_NoCache_DoesNotThrow()
    {
        // Arrange — create JimApplication without cache
        using var jimNoCache = new JimApplication(_mockRepository.Object);

        // Act & Assert — should not throw
        jimNoCache.ConnectedSystems.AddCsoToCache(1, 42, "cn=test", Guid.NewGuid());
    }

    [Test]
    public void EvictCsoFromCache_NoCache_DoesNotThrow()
    {
        // Arrange — create JimApplication without cache
        using var jimNoCache = new JimApplication(_mockRepository.Object);

        // Act & Assert — should not throw
        jimNoCache.ConnectedSystems.EvictCsoFromCache(1, 42, "cn=test");
    }

    #endregion

    #region Secondary External ID Cache Tests

    [Test]
    public async Task GetConnectedSystemObjectBySecondaryExternalIdAsync_WithAttributeId_UsesCacheAsync()
    {
        // Arrange
        var connectedSystemId = 1;
        var objectTypeId = 10;
        var secondaryExternalIdAttributeId = 55;
        var secondaryIdValue = "CN=John Smith,OU=Users,DC=corp,DC=local";
        var csoId = Guid.NewGuid();
        var expectedCso = new ConnectedSystemObject { Id = csoId };

        // Pre-populate cache with secondary external ID
        var cacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, secondaryExternalIdAttributeId, secondaryIdValue.ToLowerInvariant());
        _cache.Set(cacheKey, csoId);

        // Mock PK lookup (cache hit path)
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectAsync(connectedSystemId, csoId))
            .ReturnsAsync(expectedCso);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectBySecondaryExternalIdAsync(
            connectedSystemId, objectTypeId, secondaryIdValue, secondaryExternalIdAttributeId);

        // Assert
        Assert.That(result, Is.EqualTo(expectedCso));
        // Should NOT have called the secondary ID DB lookup
        _mockCsRepo.Verify(
            r => r.GetConnectedSystemObjectBySecondaryExternalIdAsync(connectedSystemId, objectTypeId, secondaryIdValue),
            Times.Never);
        // Should have used PK lookup from cache
        _mockCsRepo.Verify(
            r => r.GetConnectedSystemObjectAsync(connectedSystemId, csoId),
            Times.Once);
    }

    [Test]
    public async Task GetConnectedSystemObjectBySecondaryExternalIdAsync_CacheMiss_QueriesDbAndPopulatesCacheAsync()
    {
        // Arrange
        var connectedSystemId = 1;
        var objectTypeId = 10;
        var secondaryExternalIdAttributeId = 55;
        var secondaryIdValue = "CN=New User,OU=Users,DC=corp,DC=local";
        var csoId = Guid.NewGuid();
        var cso = new ConnectedSystemObject { Id = csoId };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectBySecondaryExternalIdAsync(connectedSystemId, objectTypeId, secondaryIdValue))
            .ReturnsAsync(cso);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectBySecondaryExternalIdAsync(
            connectedSystemId, objectTypeId, secondaryIdValue, secondaryExternalIdAttributeId);

        // Assert
        Assert.That(result, Is.EqualTo(cso));

        // Cache should now contain the entry
        var cacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, secondaryExternalIdAttributeId, secondaryIdValue.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(cacheKey, out Guid cachedId), Is.True);
        Assert.That(cachedId, Is.EqualTo(csoId));
    }

    [Test]
    public async Task GetConnectedSystemObjectBySecondaryExternalIdAsync_WithoutAttributeId_BypassesCacheAsync()
    {
        // Arrange
        var connectedSystemId = 1;
        var objectTypeId = 10;
        var secondaryIdValue = "CN=Test,OU=Users,DC=corp,DC=local";
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectBySecondaryExternalIdAsync(connectedSystemId, objectTypeId, secondaryIdValue))
            .ReturnsAsync(cso);

        // Act — no secondary attribute ID provided, so cache should be bypassed
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectBySecondaryExternalIdAsync(
            connectedSystemId, objectTypeId, secondaryIdValue);

        // Assert
        Assert.That(result, Is.EqualTo(cso));
        // Should have called DB directly
        _mockCsRepo.Verify(
            r => r.GetConnectedSystemObjectBySecondaryExternalIdAsync(connectedSystemId, objectTypeId, secondaryIdValue),
            Times.Once);
    }

    [Test]
    public void CacheKeySwap_EvictSecondary_AddPrimary_WorksCorrectly()
    {
        // Arrange — simulate a PendingProvisioning CSO cached by secondary ID
        var connectedSystemId = 1;
        var primaryAttributeId = 42;
        var secondaryAttributeId = 55;
        var secondaryIdValue = "CN=John Smith,OU=Users,DC=corp,DC=local";
        var primaryIdValue = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
        var csoId = Guid.NewGuid();

        // Add secondary cache entry (as provisioning would)
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, secondaryAttributeId, secondaryIdValue, csoId);

        // Verify secondary entry exists
        var secondaryCacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, secondaryAttributeId, secondaryIdValue.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(secondaryCacheKey, out Guid _), Is.True);

        // Act — simulate what confirming import does: evict secondary, add primary
        _jim.ConnectedSystems.EvictCsoFromCache(connectedSystemId, secondaryAttributeId, secondaryIdValue);
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, primaryAttributeId, primaryIdValue, csoId);

        // Assert — secondary entry should be gone, primary should exist
        Assert.That(_cache.TryGetValue(secondaryCacheKey, out Guid _), Is.False);
        var primaryCacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, primaryAttributeId, primaryIdValue.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(primaryCacheKey, out Guid cachedId), Is.True);
        Assert.That(cachedId, Is.EqualTo(csoId));
    }

    #endregion

    #region WarmCsoCacheAsync Secondary External ID Tests

    [Test]
    public async Task WarmCsoCacheAsync_IncludesSecondaryExternalIdForPendingProvisioningCsosAsync()
    {
        // Arrange — simulate a connected system with a PendingProvisioning CSO (no primary ID, has secondary)
        var connectedSystemId = 1;
        var secondaryAttributeId = 55;
        var csoId = Guid.NewGuid();

        // The repository returns the secondary ID mapping for CSOs without a primary ID
        var mappings = new Dictionary<string, Guid>
        {
            { $"cso:{connectedSystemId}:{secondaryAttributeId}:cn=provisioned user,ou=users,dc=corp,dc=local", csoId }
        };

        _mockCsRepo.Setup(r => r.GetAllCsoExternalIdMappingsAsync(connectedSystemId))
            .ReturnsAsync(mappings);

        // Act
        await _jim.ConnectedSystems.WarmCsoCacheAsync(connectedSystemId);

        // Assert — secondary ID entry should be in cache
        Assert.That(_cache.TryGetValue(mappings.First().Key, out Guid cachedId), Is.True);
        Assert.That(cachedId, Is.EqualTo(csoId));
    }

    [Test]
    public async Task WarmCsoCacheAsync_PrefersrimaryOverSecondaryAsync()
    {
        // Arrange — simulate CSOs with primary IDs AND CSOs with only secondary IDs
        var connectedSystemId = 1;
        var primaryAttributeId = 42;
        var secondaryAttributeId = 55;
        var normalCsoId = Guid.NewGuid();
        var pendingCsoId = Guid.NewGuid();

        var mappings = new Dictionary<string, Guid>
        {
            // Normal CSO with primary ID
            { $"cso:{connectedSystemId}:{primaryAttributeId}:a1b2c3d4-e5f6-7890-abcd-ef1234567890", normalCsoId },
            // PendingProvisioning CSO with only secondary ID
            { $"cso:{connectedSystemId}:{secondaryAttributeId}:cn=provisioned,ou=users,dc=corp,dc=local", pendingCsoId }
        };

        _mockCsRepo.Setup(r => r.GetAllCsoExternalIdMappingsAsync(connectedSystemId))
            .ReturnsAsync(mappings);

        // Act
        await _jim.ConnectedSystems.WarmCsoCacheAsync(connectedSystemId);

        // Assert — both entries should be in cache
        foreach (var mapping in mappings)
        {
            Assert.That(_cache.TryGetValue(mapping.Key, out Guid cachedId), Is.True);
            Assert.That(cachedId, Is.EqualTo(mapping.Value));
        }
    }

    #endregion

    #region Cache Invalidation on External ID Change Tests

    [Test]
    public void AddCsoToCache_PrimaryIdChange_OldEntryBecomesOrphaned()
    {
        // Demonstrates that without explicit eviction, changing a primary ID value
        // leaves the old cache entry orphaned (pointing to the same CSO via a stale key).
        var connectedSystemId = 1;
        var primaryAttributeId = 42;
        var oldPrimaryId = "old-guid-value";
        var newPrimaryId = "new-guid-value";
        var csoId = Guid.NewGuid();

        // Cache the CSO by its original primary ID
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, primaryAttributeId, oldPrimaryId, csoId);

        // Simulate primary ID change: add new entry without evicting old
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, primaryAttributeId, newPrimaryId, csoId);

        // Old entry still exists — orphaned and stale
        var oldCacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, primaryAttributeId, oldPrimaryId.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(oldCacheKey, out Guid oldCachedId), Is.True, "Old cache entry should still exist (orphaned)");
        Assert.That(oldCachedId, Is.EqualTo(csoId));

        // New entry also exists
        var newCacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, primaryAttributeId, newPrimaryId.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(newCacheKey, out Guid newCachedId), Is.True);
        Assert.That(newCachedId, Is.EqualTo(csoId));
    }

    [Test]
    public void EvictAndReplace_PrimaryIdChange_OldEntryRemoved()
    {
        // Verifies the correct evict-then-add pattern for primary ID changes.
        var connectedSystemId = 1;
        var primaryAttributeId = 42;
        var oldPrimaryId = "old-guid-value";
        var newPrimaryId = "new-guid-value";
        var csoId = Guid.NewGuid();

        // Cache the CSO by its original primary ID
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, primaryAttributeId, oldPrimaryId, csoId);

        // Simulate correct primary ID change: evict old, then add new
        _jim.ConnectedSystems.EvictCsoFromCache(connectedSystemId, primaryAttributeId, oldPrimaryId);
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, primaryAttributeId, newPrimaryId, csoId);

        // Old entry should be gone
        var oldCacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, primaryAttributeId, oldPrimaryId.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(oldCacheKey, out Guid _), Is.False, "Old primary cache entry should be evicted");

        // New entry should exist
        var newCacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, primaryAttributeId, newPrimaryId.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(newCacheKey, out Guid cachedId), Is.True);
        Assert.That(cachedId, Is.EqualTo(csoId));
    }

    [Test]
    public void EvictAndReplace_SecondaryIdChange_OldEntryRemoved()
    {
        // Verifies evict-then-add pattern for secondary ID changes (e.g. DN rename).
        var connectedSystemId = 1;
        var secondaryAttributeId = 55;
        var oldDn = "CN=John Smith,OU=Users,DC=corp,DC=local";
        var newDn = "CN=John E. Smith,OU=Users,DC=corp,DC=local";
        var csoId = Guid.NewGuid();

        // Cache the CSO by its original DN
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, secondaryAttributeId, oldDn, csoId);

        // Simulate DN rename: evict old, add new
        _jim.ConnectedSystems.EvictCsoFromCache(connectedSystemId, secondaryAttributeId, oldDn);
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, secondaryAttributeId, newDn, csoId);

        // Old DN entry should be gone
        var oldCacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, secondaryAttributeId, oldDn.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(oldCacheKey, out Guid _), Is.False, "Old DN cache entry should be evicted after rename");

        // New DN entry should exist
        var newCacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, secondaryAttributeId, newDn.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(newCacheKey, out Guid cachedId), Is.True);
        Assert.That(cachedId, Is.EqualTo(csoId));
    }

    [Test]
    public void CacheKeySwap_SecondaryIdRename_OldDnEvicted_NewDnCached()
    {
        // End-to-end DN rename scenario: object provisioned with old DN, then DN changes
        // in LDAP, confirming import brings in the new DN.
        var connectedSystemId = 1;
        var primaryAttributeId = 42;
        var secondaryAttributeId = 55;
        var primaryId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
        var oldDn = "CN=John Smith,OU=Users,DC=corp,DC=local";
        var newDn = "CN=John E. Smith,OU=Senior Staff,DC=corp,DC=local";
        var csoId = Guid.NewGuid();

        // Step 1: CSO is Normal, cached by primary ID and old secondary (DN)
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, primaryAttributeId, primaryId, csoId);
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, secondaryAttributeId, oldDn, csoId);

        // Step 2: Import detects DN rename — evict old secondary, add new secondary
        _jim.ConnectedSystems.EvictCsoFromCache(connectedSystemId, secondaryAttributeId, oldDn);
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, secondaryAttributeId, newDn, csoId);

        // Primary should be untouched
        var primaryCacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, primaryAttributeId, primaryId.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(primaryCacheKey, out Guid primaryCachedId), Is.True, "Primary cache entry should still exist");
        Assert.That(primaryCachedId, Is.EqualTo(csoId));

        // Old DN should be gone
        var oldDnCacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, secondaryAttributeId, oldDn.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(oldDnCacheKey, out Guid _), Is.False, "Old DN cache entry should be evicted");

        // New DN should exist
        var newDnCacheKey = ConnectedSystemServer.BuildCsoCacheKey(connectedSystemId, secondaryAttributeId, newDn.ToLowerInvariant());
        Assert.That(_cache.TryGetValue(newDnCacheKey, out Guid newDnCachedId), Is.True, "New DN cache entry should exist");
        Assert.That(newDnCachedId, Is.EqualTo(csoId));
    }

    [Test]
    public async Task AddCsoToCache_DifferentCsoReusesOldExternalId_ReturnsNewCsoAsync()
    {
        // After proper eviction and re-add, a reused external ID value should resolve to the new CSO.
        // Scenario: Object A had DN "CN=ServiceAccount,OU=...", Object A is deleted,
        // Object B is created and assigned the same DN.
        var connectedSystemId = 1;
        var attributeId = 55;
        var sharedDn = "CN=ServiceAccount,OU=Services,DC=corp,DC=local";
        var csoA = Guid.NewGuid();
        var csoB = Guid.NewGuid();
        var expectedCsoB = new ConnectedSystemObject { Id = csoB };

        // Object A cached by DN
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, attributeId, sharedDn, csoA);

        // Object A deleted — evict its cache entry
        _jim.ConnectedSystems.EvictCsoFromCache(connectedSystemId, attributeId, sharedDn);

        // Object B created with same DN
        _jim.ConnectedSystems.AddCsoToCache(connectedSystemId, attributeId, sharedDn, csoB);

        // Mock PK lookup for Object B
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectAsync(connectedSystemId, csoB))
            .ReturnsAsync(expectedCsoB);

        // Lookup by DN should return Object B, not Object A
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectBySecondaryExternalIdAsync(
            connectedSystemId, 10, sharedDn, attributeId);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(csoB), "Reused external ID should resolve to the new CSO after proper eviction");

        // Should NOT have called DB — resolved from cache
        _mockCsRepo.Verify(
            r => r.GetConnectedSystemObjectBySecondaryExternalIdAsync(connectedSystemId, 10, sharedDn),
            Times.Never);
    }

    #endregion
}
