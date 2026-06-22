// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Activities;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

[TestFixture]
public class MetaverseServerAttributeValidationTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();

        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);

        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);

        _jim = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _jim.Dispose();
    }

    #region DeleteMetaverseAttributeAsync validation

    [Test]
    public async Task DeleteMetaverseAttributeAsync_WithSyncRuleReferences_ThrowsMetaverseAttributeInUseExceptionAsync()
    {
        // Arrange
        var attribute = new MetaverseAttribute { Id = 42, Name = "costCentre" };
        var syncRuleRefs = new List<SyncRuleReference>
        {
            new() { Id = 1, Name = "Import Users from LDAP" },
            new() { Id = 2, Name = "Export Users to LDAP" }
        };

        _mockMetaverseRepo
            .Setup(r => r.GetSyncRulesReferencingAttributeAsync(attribute.Id))
            .ReturnsAsync(syncRuleRefs);

        // Act & Assert
        var ex = Assert.ThrowsAsync<MetaverseAttributeInUseException>(
            () => _jim.Metaverse.DeleteMetaverseAttributeAsync(attribute, (MetaverseObject?)null));

        Assert.That(ex!.Message, Does.Contain("costCentre"));
        Assert.That(ex.Message, Does.Contain("2 Sync Rule(s)"));
        Assert.That(ex.Message, Does.Contain("Import Users from LDAP"));
        Assert.That(ex.Message, Does.Contain("Export Users to LDAP"));
        Assert.That(ex.ReferencingSyncRules, Has.Count.EqualTo(2));

        // Verify delete was NOT called
        _mockMetaverseRepo.Verify(
            r => r.DeleteMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()), Times.Never);
    }

    [Test]
    public async Task DeleteMetaverseAttributeAsync_WithStoredValues_ThrowsMetaverseAttributeInUseExceptionAsync()
    {
        // Arrange
        var attribute = new MetaverseAttribute { Id = 42, Name = "costCentre" };

        _mockMetaverseRepo
            .Setup(r => r.GetSyncRulesReferencingAttributeAsync(attribute.Id))
            .ReturnsAsync(new List<SyncRuleReference>());

        _mockMetaverseRepo
            .Setup(r => r.GetAttributeValueObjectCountAsync(attribute.Id))
            .ReturnsAsync(1523);

        // Act & Assert
        var ex = Assert.ThrowsAsync<MetaverseAttributeInUseException>(
            () => _jim.Metaverse.DeleteMetaverseAttributeAsync(attribute, (MetaverseObject?)null));

        Assert.That(ex!.Message, Does.Contain("costCentre"));
        Assert.That(ex.Message, Does.Contain("1,523 Metaverse Object(s)"));
        Assert.That(ex.AffectedObjectCount, Is.EqualTo(1523));

        // Verify delete was NOT called
        _mockMetaverseRepo.Verify(
            r => r.DeleteMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()), Times.Never);
    }

    [Test]
    public async Task DeleteMetaverseAttributeAsync_WithSyncRulesAndStoredValues_ChecksSyncRulesFirstAsync()
    {
        // Arrange: attribute has both Sync Rule references AND stored values
        var attribute = new MetaverseAttribute { Id = 42, Name = "costCentre" };
        var syncRuleRefs = new List<SyncRuleReference>
        {
            new() { Id = 1, Name = "Import Users from LDAP" }
        };

        _mockMetaverseRepo
            .Setup(r => r.GetSyncRulesReferencingAttributeAsync(attribute.Id))
            .ReturnsAsync(syncRuleRefs);

        _mockMetaverseRepo
            .Setup(r => r.GetAttributeValueObjectCountAsync(attribute.Id))
            .ReturnsAsync(500);

        // Act & Assert: Sync Rule check takes priority
        var ex = Assert.ThrowsAsync<MetaverseAttributeInUseException>(
            () => _jim.Metaverse.DeleteMetaverseAttributeAsync(attribute, (MetaverseObject?)null));

        Assert.That(ex!.Message, Does.Contain("Sync Rule(s)"));
        Assert.That(ex.ReferencingSyncRules, Has.Count.EqualTo(1));

        // Value count should NOT have been checked since Sync Rule check failed first
        _mockMetaverseRepo.Verify(
            r => r.GetAttributeValueObjectCountAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task DeleteMetaverseAttributeAsync_WithNoReferencesOrValues_SucceedsAsync()
    {
        // Arrange
        var attribute = new MetaverseAttribute { Id = 42, Name = "costCentre" };
        var initiatedBy = TestUtilities.GetInitiatedBy();

        _mockMetaverseRepo
            .Setup(r => r.GetSyncRulesReferencingAttributeAsync(attribute.Id))
            .ReturnsAsync(new List<SyncRuleReference>());

        _mockMetaverseRepo
            .Setup(r => r.GetAttributeValueObjectCountAsync(attribute.Id))
            .ReturnsAsync(0);

        _mockMetaverseRepo
            .Setup(r => r.DeleteMetaverseAttributeAsync(attribute))
            .Returns(Task.CompletedTask);

        // Act
        await _jim.Metaverse.DeleteMetaverseAttributeAsync(attribute, initiatedBy);

        // Assert: delete was called
        _mockMetaverseRepo.Verify(
            r => r.DeleteMetaverseAttributeAsync(attribute), Times.Once);
    }

    [Test]
    public async Task DeleteMetaverseAttributeAsync_ApiKeyOverload_WithSyncRuleReferences_ThrowsAsync()
    {
        // Arrange
        var attribute = new MetaverseAttribute { Id = 42, Name = "costCentre" };
        var apiKey = new JIM.Models.Security.ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "TestKey",
            KeyHash = "hash",
            KeyPrefix = "pfx",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };
        var syncRuleRefs = new List<SyncRuleReference>
        {
            new() { Id = 1, Name = "Import Users" }
        };

        _mockMetaverseRepo
            .Setup(r => r.GetSyncRulesReferencingAttributeAsync(attribute.Id))
            .ReturnsAsync(syncRuleRefs);

        // Act & Assert
        var ex = Assert.ThrowsAsync<MetaverseAttributeInUseException>(
            () => _jim.Metaverse.DeleteMetaverseAttributeAsync(attribute, apiKey));

        Assert.That(ex!.ReferencingSyncRules, Has.Count.EqualTo(1));
    }

    #endregion

    #region ValidateObjectTypeRemovalAsync

    [Test]
    public async Task ValidateObjectTypeRemovalAsync_WithValuesForRemovedType_ThrowsMetaverseAttributeInUseExceptionAsync()
    {
        // Arrange
        var personType = new MetaverseObjectType { Id = 1, Name = "person" };
        var groupType = new MetaverseObjectType { Id = 2, Name = "group" };
        var attribute = new MetaverseAttribute
        {
            Id = 42,
            Name = "costCentre",
            MetaverseObjectTypes = new List<MetaverseObjectType> { personType, groupType }
        };

        // Removing "person" (keeping only "group")
        var newObjectTypeIds = new List<int> { 2 };

        _mockMetaverseRepo
            .Setup(r => r.GetAttributeValueObjectCountByTypeAsync(attribute.Id, personType.Id))
            .ReturnsAsync(450);

        _mockMetaverseRepo
            .Setup(r => r.GetMetaverseObjectTypeAsync(personType.Id, false))
            .ReturnsAsync(personType);

        // Act & Assert
        var ex = Assert.ThrowsAsync<MetaverseAttributeInUseException>(
            () => _jim.Metaverse.ValidateObjectTypeRemovalAsync(attribute, newObjectTypeIds));

        Assert.That(ex!.Message, Does.Contain("person"));
        Assert.That(ex.Message, Does.Contain("costCentre"));
        Assert.That(ex.Message, Does.Contain("450"));
        Assert.That(ex.AffectedObjectCount, Is.EqualTo(450));
    }

    [Test]
    public async Task ValidateObjectTypeRemovalAsync_WithNoValuesForRemovedType_SucceedsAsync()
    {
        // Arrange
        var personType = new MetaverseObjectType { Id = 1, Name = "person" };
        var groupType = new MetaverseObjectType { Id = 2, Name = "group" };
        var attribute = new MetaverseAttribute
        {
            Id = 42,
            Name = "costCentre",
            MetaverseObjectTypes = new List<MetaverseObjectType> { personType, groupType }
        };

        // Removing "person" (keeping only "group")
        var newObjectTypeIds = new List<int> { 2 };

        _mockMetaverseRepo
            .Setup(r => r.GetAttributeValueObjectCountByTypeAsync(attribute.Id, personType.Id))
            .ReturnsAsync(0);

        _mockMetaverseRepo
            .Setup(r => r.GetMetaverseObjectTypeAsync(personType.Id, false))
            .ReturnsAsync(personType);

        // Act & Assert: should not throw
        Assert.DoesNotThrowAsync(
            () => _jim.Metaverse.ValidateObjectTypeRemovalAsync(attribute, newObjectTypeIds));
    }

    [Test]
    public async Task ValidateObjectTypeRemovalAsync_AddingObjectTypes_SucceedsAsync()
    {
        // Arrange: attribute currently has "person", adding "group"
        var personType = new MetaverseObjectType { Id = 1, Name = "person" };
        var attribute = new MetaverseAttribute
        {
            Id = 42,
            Name = "costCentre",
            MetaverseObjectTypes = new List<MetaverseObjectType> { personType }
        };

        // Adding "group" (keeping "person")
        var newObjectTypeIds = new List<int> { 1, 2 };

        // Act & Assert: no removals, should not throw or query for values
        Assert.DoesNotThrowAsync(
            () => _jim.Metaverse.ValidateObjectTypeRemovalAsync(attribute, newObjectTypeIds));

        _mockMetaverseRepo.Verify(
            r => r.GetAttributeValueObjectCountByTypeAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    #endregion
}
