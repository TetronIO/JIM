using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

[TestFixture]
public class MetaverseServerChangeTrackingTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private JimApplication _jim = null!;

    private MetaverseObjectType _userType = null!;
    private MetaverseAttribute _displayNameAttr = null!;
    private MetaverseAttribute _departmentAttr = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();

        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);

        // Default: change tracking is enabled (GetSettingAsync returns null => default of true)
        _mockServiceSettingsRepo
            .Setup(r => r.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((ServiceSetting?)null);

        _jim = new JimApplication(_mockRepository.Object);

        _userType = new MetaverseObjectType { Id = 1, Name = "User" };
        _displayNameAttr = new MetaverseAttribute
        {
            Id = 1,
            Name = "DisplayName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _departmentAttr = new MetaverseAttribute
        {
            Id = 2,
            Name = "Department",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    #region CreateMetaverseObjectAsync change tracking

    [Test]
    public async Task CreateMetaverseObjectAsync_WithChangeTrackingEnabled_CreatesChangeRecordAsync()
    {
        // Arrange
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = _displayNameAttr,
            StringValue = "Alice Adams"
        });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = _departmentAttr,
            StringValue = "Engineering"
        });
        var userId = Guid.NewGuid();

        // Act
        await _jim.Metaverse.CreateMetaverseObjectAsync(
            mvo,
            ActivityInitiatorType.User,
            userId,
            "Test User",
            MetaverseObjectChangeInitiatorType.ExampleData);

        // Assert
        Assert.That(mvo.Changes, Has.Count.EqualTo(1));
        var change = mvo.Changes[0];
        Assert.That(change.ChangeType, Is.EqualTo(ObjectChangeType.Created));
        Assert.That(change.ChangeInitiatorType, Is.EqualTo(MetaverseObjectChangeInitiatorType.ExampleData));
        Assert.That(change.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(change.InitiatedById, Is.EqualTo(userId));
        Assert.That(change.InitiatedByName, Is.EqualTo("Test User"));
        Assert.That(change.AttributeChanges, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task CreateMetaverseObjectAsync_AllAttributeValuesAreAdditionsAsync()
    {
        // Arrange
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = _displayNameAttr,
            StringValue = "Bob Brown"
        });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = _departmentAttr,
            StringValue = "Finance"
        });

        // Act
        await _jim.Metaverse.CreateMetaverseObjectAsync(
            mvo,
            changeInitiatorType: MetaverseObjectChangeInitiatorType.ExampleData);

        // Assert
        var change = mvo.Changes[0];
        foreach (var attrChange in change.AttributeChanges)
        {
            Assert.That(attrChange.ValueChanges, Has.Count.EqualTo(1));
            Assert.That(attrChange.ValueChanges[0].ValueChangeType, Is.EqualTo(ValueChangeType.Add));
        }

        var displayNameChange = change.AttributeChanges.Single(ac => ac.Attribute!.Id == _displayNameAttr.Id);
        Assert.That(displayNameChange.ValueChanges[0].StringValue, Is.EqualTo("Bob Brown"));

        var departmentChange = change.AttributeChanges.Single(ac => ac.Attribute!.Id == _departmentAttr.Id);
        Assert.That(departmentChange.ValueChanges[0].StringValue, Is.EqualTo("Finance"));
    }

    [Test]
    public async Task CreateMetaverseObjectAsync_WhenChangeTrackingDisabled_DoesNotCreateChangeRecordAsync()
    {
        // Arrange - return a setting that disables change tracking
        var disabledSetting = new ServiceSetting
        {
            Key = "ChangeTracking.MvoChangesEnabled",
            DefaultValue = "True",
            Value = "False",
            ValueType = ServiceSettingValueType.Boolean
        };
        _mockServiceSettingsRepo
            .Setup(r => r.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync(disabledSetting);

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = _displayNameAttr,
            StringValue = "Charlie Clark"
        });

        // Act
        await _jim.Metaverse.CreateMetaverseObjectAsync(
            mvo,
            changeInitiatorType: MetaverseObjectChangeInitiatorType.ExampleData);

        // Assert
        Assert.That(mvo.Changes, Has.Count.EqualTo(0));
    }

    [Test]
    public async Task CreateMetaverseObjectAsync_WithNoAttributes_DoesNotCreateChangeRecordAsync()
    {
        // Arrange - MVO with no attribute values
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };

        // Act
        await _jim.Metaverse.CreateMetaverseObjectAsync(
            mvo,
            changeInitiatorType: MetaverseObjectChangeInitiatorType.System);

        // Assert
        Assert.That(mvo.Changes, Has.Count.EqualTo(0));
    }

    [Test]
    public async Task CreateMetaverseObjectAsync_WithSystemInitiator_SetsCorrectChangeInitiatorTypeAsync()
    {
        // Arrange
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = _displayNameAttr,
            StringValue = "Admin User"
        });

        // Act
        await _jim.Metaverse.CreateMetaverseObjectAsync(
            mvo,
            changeInitiatorType: MetaverseObjectChangeInitiatorType.System);

        // Assert
        var change = mvo.Changes[0];
        Assert.That(change.ChangeType, Is.EqualTo(ObjectChangeType.Created));
        Assert.That(change.ChangeInitiatorType, Is.EqualTo(MetaverseObjectChangeInitiatorType.System));
    }

    [Test]
    public async Task CreateMetaverseObjectAsync_PersistsViaRepositoryAsync()
    {
        // Arrange
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = _displayNameAttr,
            StringValue = "Test User"
        });

        // Act
        await _jim.Metaverse.CreateMetaverseObjectAsync(
            mvo,
            changeInitiatorType: MetaverseObjectChangeInitiatorType.System);

        // Assert - verify the repository was called
        _mockMetaverseRepo.Verify(
            r => r.CreateMetaverseObjectAsync(mvo),
            Times.Once);
    }

    #endregion

    #region UpdateMetaverseObjectAsync change tracking

    [Test]
    public async Task UpdateMetaverseObjectAsync_WithAdditions_CreatesChangeRecordAsync()
    {
        // Arrange
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        var additions = new List<MetaverseObjectAttributeValue>
        {
            new() { Attribute = _displayNameAttr, StringValue = "New Name" }
        };
        var removals = new List<MetaverseObjectAttributeValue>
        {
            new() { Attribute = _displayNameAttr, StringValue = "Old Name" }
        };
        var userId = Guid.NewGuid();

        // Act
        await _jim.Metaverse.UpdateMetaverseObjectAsync(
            mvo,
            additions,
            removals,
            ActivityInitiatorType.User,
            userId,
            "Test User",
            MetaverseObjectChangeInitiatorType.User);

        // Assert
        Assert.That(mvo.Changes, Has.Count.EqualTo(1));
        var change = mvo.Changes[0];
        Assert.That(change.ChangeType, Is.EqualTo(ObjectChangeType.Updated));
        Assert.That(change.ChangeInitiatorType, Is.EqualTo(MetaverseObjectChangeInitiatorType.User));
        Assert.That(change.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(change.InitiatedById, Is.EqualTo(userId));
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_WithoutAdditionsOrRemovals_SkipsChangeTrackingAsync()
    {
        // Arrange - operational update (e.g. marking for deletion), no attribute changes
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };

        // Act - call without additions/removals
        await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo);

        // Assert - no change record created, but repository was still called
        Assert.That(mvo.Changes, Has.Count.EqualTo(0));
        _mockMetaverseRepo.Verify(
            r => r.UpdateMetaverseObjectAsync(mvo),
            Times.Once);
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_WhenChangeTrackingDisabled_DoesNotCreateChangeRecordAsync()
    {
        // Arrange
        var disabledSetting = new ServiceSetting
        {
            Key = "ChangeTracking.MvoChangesEnabled",
            DefaultValue = "True",
            Value = "False",
            ValueType = ServiceSettingValueType.Boolean
        };
        _mockServiceSettingsRepo
            .Setup(r => r.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync(disabledSetting);

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        var additions = new List<MetaverseObjectAttributeValue>
        {
            new() { Attribute = _displayNameAttr, StringValue = "New Name" }
        };

        // Act
        await _jim.Metaverse.UpdateMetaverseObjectAsync(
            mvo,
            additions: additions,
            changeInitiatorType: MetaverseObjectChangeInitiatorType.System);

        // Assert - no change record, but update still persisted
        Assert.That(mvo.Changes, Has.Count.EqualTo(0));
        _mockMetaverseRepo.Verify(
            r => r.UpdateMetaverseObjectAsync(mvo),
            Times.Once);
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_WithEmptyAdditionsAndRemovals_DoesNotCreateChangeRecordAsync()
    {
        // Arrange
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };

        // Act - explicitly pass empty lists (different from null/not provided)
        await _jim.Metaverse.UpdateMetaverseObjectAsync(
            mvo,
            additions: new List<MetaverseObjectAttributeValue>(),
            removals: new List<MetaverseObjectAttributeValue>(),
            changeInitiatorType: MetaverseObjectChangeInitiatorType.System);

        // Assert - no change record because no actual changes
        Assert.That(mvo.Changes, Has.Count.EqualTo(0));
        _mockMetaverseRepo.Verify(
            r => r.UpdateMetaverseObjectAsync(mvo),
            Times.Once);
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_DeriveUserInitiatorType_WhenNotExplicitlySetAsync()
    {
        // Arrange
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        var userId = Guid.NewGuid();
        var additions = new List<MetaverseObjectAttributeValue>
        {
            new() { Attribute = _displayNameAttr, StringValue = "New Name" }
        };

        // Act - pass User initiator type but no explicit changeInitiatorType
        await _jim.Metaverse.UpdateMetaverseObjectAsync(
            mvo,
            additions: additions,
            initiatedByType: ActivityInitiatorType.User,
            initiatedById: userId,
            initiatedByName: "Test User");

        // Assert - should derive User from ActivityInitiatorType
        var change = mvo.Changes[0];
        Assert.That(change.ChangeInitiatorType, Is.EqualTo(MetaverseObjectChangeInitiatorType.User));
    }

    #endregion

    #region DeleteMetaverseObjectAsync change tracking

    [Test]
    public async Task DeleteMetaverseObjectAsync_WithPreCapturedAttributes_CapturesFinalValuesAsync()
    {
        // Arrange - simulates the sync processor path where attribute values are
        // snapshotted before attribute recall removes them from the MVO.
        // MVO's AttributeValues is empty (attributes were already recalled) so DisplayName is null.
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };

        // Pre-captured attribute values (snapshotted before recall)
        var finalAttributeValues = new List<MetaverseObjectAttributeValue>
        {
            new() { Attribute = _displayNameAttr, StringValue = "Alice Adams" },
            new() { Attribute = _departmentAttr, StringValue = "Engineering" }
        };

        var userId = Guid.NewGuid();

        MetaverseObjectChange? capturedChange = null;
        _mockMetaverseRepo
            .Setup(r => r.CreateMetaverseObjectChangeDirectAsync(It.IsAny<MetaverseObjectChange>()))
            .Callback<MetaverseObjectChange>(c => capturedChange = c)
            .Returns(Task.CompletedTask);

        // Act
        await _jim.Metaverse.DeleteMetaverseObjectAsync(
            mvo,
            ActivityInitiatorType.User,
            userId,
            "Test User",
            finalAttributeValues);

        // Assert - change record was created with final attribute values as removals
        Assert.That(capturedChange, Is.Not.Null);
        Assert.That(capturedChange!.ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
        Assert.That(capturedChange.DeletedObjectDisplayName, Is.Null);
        Assert.That(capturedChange.DeletedObjectTypeId, Is.EqualTo(_userType.Id));
        Assert.That(capturedChange.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(capturedChange.InitiatedById, Is.EqualTo(userId));
        Assert.That(capturedChange.InitiatedByName, Is.EqualTo("Test User"));

        // Two attributes should be captured
        Assert.That(capturedChange.AttributeChanges, Has.Count.EqualTo(2));

        // All values should be recorded as removals (final state before deletion)
        foreach (var attrChange in capturedChange.AttributeChanges)
        {
            Assert.That(attrChange.ValueChanges, Has.Count.EqualTo(1));
            Assert.That(attrChange.ValueChanges[0].ValueChangeType, Is.EqualTo(ValueChangeType.Remove));
        }

        var displayNameChange = capturedChange.AttributeChanges.Single(ac => ac.Attribute!.Id == _displayNameAttr.Id);
        Assert.That(displayNameChange.ValueChanges[0].StringValue, Is.EqualTo("Alice Adams"));

        var departmentChange = capturedChange.AttributeChanges.Single(ac => ac.Attribute!.Id == _departmentAttr.Id);
        Assert.That(departmentChange.ValueChanges[0].StringValue, Is.EqualTo("Engineering"));

        // LoadMetaverseObjectAttributeValuesAsync should NOT be called when pre-captured values are provided
        _mockMetaverseRepo.Verify(
            r => r.LoadMetaverseObjectAttributeValuesAsync(It.IsAny<MetaverseObject>()),
            Times.Never);

        // MVO itself was still deleted
        _mockMetaverseRepo.Verify(r => r.DeleteMetaverseObjectAsync(mvo), Times.Once);
    }

    [Test]
    public async Task DeleteMetaverseObjectAsync_WithoutPreCapturedAttributes_UsesCurrentValuesAsync()
    {
        // Arrange - simulates the housekeeping path where MVO still has its attributes
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = _displayNameAttr,
            StringValue = "Bob Brown"
        });

        MetaverseObjectChange? capturedChange = null;
        _mockMetaverseRepo
            .Setup(r => r.CreateMetaverseObjectChangeDirectAsync(It.IsAny<MetaverseObjectChange>()))
            .Callback<MetaverseObjectChange>(c => capturedChange = c)
            .Returns(Task.CompletedTask);

        // Act - no finalAttributeValues passed
        await _jim.Metaverse.DeleteMetaverseObjectAsync(mvo);

        // Assert - attribute values captured from MVO
        Assert.That(capturedChange, Is.Not.Null);
        Assert.That(capturedChange!.AttributeChanges, Has.Count.EqualTo(1));

        var displayNameChange = capturedChange.AttributeChanges.Single(ac => ac.Attribute!.Id == _displayNameAttr.Id);
        Assert.That(displayNameChange.ValueChanges[0].StringValue, Is.EqualTo("Bob Brown"));
        Assert.That(displayNameChange.ValueChanges[0].ValueChangeType, Is.EqualTo(ValueChangeType.Remove));

        // LoadMetaverseObjectAttributeValuesAsync should NOT be called since values were already present
        _mockMetaverseRepo.Verify(
            r => r.LoadMetaverseObjectAttributeValuesAsync(It.IsAny<MetaverseObject>()),
            Times.Never);
    }

    [Test]
    public async Task DeleteMetaverseObjectAsync_WhenChangeTrackingDisabled_DoesNotCaptureAttributesAsync()
    {
        // Arrange
        var disabledSetting = new ServiceSetting
        {
            Key = "ChangeTracking.MvoChangesEnabled",
            DefaultValue = "True",
            Value = "False",
            ValueType = ServiceSettingValueType.Boolean
        };
        _mockServiceSettingsRepo
            .Setup(r => r.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync(disabledSetting);

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = _displayNameAttr,
            StringValue = "Charlie Clark"
        });

        // Act
        await _jim.Metaverse.DeleteMetaverseObjectAsync(mvo);

        // Assert - no change record created, but MVO still deleted
        _mockMetaverseRepo.Verify(
            r => r.CreateMetaverseObjectChangeDirectAsync(It.IsAny<MetaverseObjectChange>()),
            Times.Never);
        _mockMetaverseRepo.Verify(
            r => r.DeleteMetaverseObjectAsync(mvo),
            Times.Once);
    }

    [Test]
    public async Task DeleteMetaverseObjectAsync_WithEmptyAttributes_CreatesChangeRecordWithoutAttributeChangesAsync()
    {
        // Arrange - MVO with no attribute values and none to load from DB
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };

        // LoadMetaverseObjectAttributeValuesAsync is called but doesn't add any values
        _mockMetaverseRepo
            .Setup(r => r.LoadMetaverseObjectAttributeValuesAsync(mvo))
            .Returns(Task.CompletedTask);

        MetaverseObjectChange? capturedChange = null;
        _mockMetaverseRepo
            .Setup(r => r.CreateMetaverseObjectChangeDirectAsync(It.IsAny<MetaverseObjectChange>()))
            .Callback<MetaverseObjectChange>(c => capturedChange = c)
            .Returns(Task.CompletedTask);

        // Act
        await _jim.Metaverse.DeleteMetaverseObjectAsync(mvo);

        // Assert - change record created but with no attribute changes
        Assert.That(capturedChange, Is.Not.Null);
        Assert.That(capturedChange!.ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
        Assert.That(capturedChange.AttributeChanges, Has.Count.EqualTo(0));
    }

    [Test]
    public async Task DeleteMetaverseObjectAsync_LoadsAttributeValues_WhenNotAlreadyLoadedAsync()
    {
        // Arrange - MVO with empty AttributeValues (simulates housekeeping path where
        // GetMetaverseObjectsEligibleForDeletionAsync doesn't include AttributeValues)
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };

        // When LoadMetaverseObjectAttributeValuesAsync is called, populate the attribute values
        _mockMetaverseRepo
            .Setup(r => r.LoadMetaverseObjectAttributeValuesAsync(mvo))
            .Callback<MetaverseObject>(m =>
            {
                m.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Attribute = _displayNameAttr,
                    StringValue = "Lazy Loaded"
                });
            })
            .Returns(Task.CompletedTask);

        MetaverseObjectChange? capturedChange = null;
        _mockMetaverseRepo
            .Setup(r => r.CreateMetaverseObjectChangeDirectAsync(It.IsAny<MetaverseObjectChange>()))
            .Callback<MetaverseObjectChange>(c => capturedChange = c)
            .Returns(Task.CompletedTask);

        // Act
        await _jim.Metaverse.DeleteMetaverseObjectAsync(mvo);

        // Assert - LoadMetaverseObjectAttributeValuesAsync was called
        _mockMetaverseRepo.Verify(
            r => r.LoadMetaverseObjectAttributeValuesAsync(mvo),
            Times.Once);

        // Assert - attribute values were captured after loading
        Assert.That(capturedChange, Is.Not.Null);
        Assert.That(capturedChange!.AttributeChanges, Has.Count.EqualTo(1));
        var displayNameChange = capturedChange.AttributeChanges.Single(ac => ac.Attribute!.Id == _displayNameAttr.Id);
        Assert.That(displayNameChange.ValueChanges[0].StringValue, Is.EqualTo("Lazy Loaded"));
        Assert.That(displayNameChange.ValueChanges[0].ValueChangeType, Is.EqualTo(ValueChangeType.Remove));
    }

    #endregion

    #region Attribute name/type snapshot (Issue #58)

    [Test]
    public async Task CreateMetaverseObjectAsync_PopulatesAttributeNameAndTypeOnChangeAttributeAsync()
    {
        // Arrange
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = _displayNameAttr,
            StringValue = "Alice Adams"
        });

        // Act
        await _jim.Metaverse.CreateMetaverseObjectAsync(
            mvo,
            changeInitiatorType: MetaverseObjectChangeInitiatorType.ExampleData);

        // Assert - sibling properties populated from the attribute definition
        var attrChange = mvo.Changes[0].AttributeChanges.Single();
        Assert.That(attrChange.AttributeName, Is.EqualTo("DisplayName"));
        Assert.That(attrChange.AttributeType, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_PopulatesAttributeNameAndTypeOnChangeAttributeAsync()
    {
        // Arrange
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        var additions = new List<MetaverseObjectAttributeValue>
        {
            new() { Attribute = _departmentAttr, StringValue = "Engineering" }
        };

        // Act
        await _jim.Metaverse.UpdateMetaverseObjectAsync(
            mvo,
            additions: additions,
            changeInitiatorType: MetaverseObjectChangeInitiatorType.System);

        // Assert
        var attrChange = mvo.Changes[0].AttributeChanges.Single();
        Assert.That(attrChange.AttributeName, Is.EqualTo("Department"));
        Assert.That(attrChange.AttributeType, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public async Task DeleteMetaverseObjectAsync_PopulatesAttributeNameAndTypeOnChangeAttributeAsync()
    {
        // Arrange
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        var finalAttributeValues = new List<MetaverseObjectAttributeValue>
        {
            new() { Attribute = _displayNameAttr, StringValue = "Alice Adams" }
        };

        MetaverseObjectChange? capturedChange = null;
        _mockMetaverseRepo
            .Setup(r => r.CreateMetaverseObjectChangeDirectAsync(It.IsAny<MetaverseObjectChange>()))
            .Callback<MetaverseObjectChange>(c => capturedChange = c)
            .Returns(Task.CompletedTask);

        // Act
        await _jim.Metaverse.DeleteMetaverseObjectAsync(mvo, finalAttributeValues: finalAttributeValues);

        // Assert
        var attrChange = capturedChange!.AttributeChanges.Single();
        Assert.That(attrChange.AttributeName, Is.EqualTo("DisplayName"));
        Assert.That(attrChange.AttributeType, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void AddMvoChangeAttributeValueObject_WhenAttributeIsNull_SiblingPropertiesStillAvailable()
    {
        // Arrange - simulate a change attribute where the FK has been set to null (attribute deleted)
        var change = new MetaverseObjectChange();
        var attrChange = new MetaverseObjectChangeAttribute
        {
            Attribute = null,
            AttributeName = "DeletedAttribute",
            AttributeType = AttributeDataType.Text,
            MetaverseObjectChange = change
        };

        // Assert - sibling properties are accessible even with null Attribute
        Assert.That(attrChange.AttributeName, Is.EqualTo("DeletedAttribute"));
        Assert.That(attrChange.AttributeType, Is.EqualTo(AttributeDataType.Text));
        Assert.That(attrChange.Attribute, Is.Null);
    }

    [Test]
    public void AddMvoChangeAttributeValueObject_ReferenceWithLoadedNavigation_RecordsReferenceAsync()
    {
        // Arrange
        var change = new MetaverseObjectChange();
        var referencedMvo = new MetaverseObject { Id = Guid.NewGuid() };
        var refAttr = new MetaverseAttribute { Id = 10, Name = "Manager", Type = AttributeDataType.Reference };
        var attrValue = new MetaverseObjectAttributeValue
        {
            Attribute = refAttr,
            ReferenceValue = referencedMvo,
            ReferenceValueId = referencedMvo.Id
        };

        // Act
        JIM.Application.Servers.MetaverseServer.AddMvoChangeAttributeValueObject(change, attrValue, ValueChangeType.Add);

        // Assert
        var attrChange = change.AttributeChanges.Single();
        Assert.That(attrChange.AttributeName, Is.EqualTo("Manager"));
        var valueChange = attrChange.ValueChanges.Single();
        Assert.That(valueChange.ReferenceValue, Is.EqualTo(referencedMvo));
    }

    [Test]
    public void AddMvoChangeAttributeValueObject_ReferenceWithFkOnly_RecordsGuidAsync()
    {
        // Arrange — navigation property not loaded but FK is set (common during MVO deletion
        // when referenced MVOs are not in the EF change tracker)
        var change = new MetaverseObjectChange();
        var referencedMvoId = Guid.NewGuid();
        var refAttr = new MetaverseAttribute { Id = 10, Name = "Manager", Type = AttributeDataType.Reference };
        var attrValue = new MetaverseObjectAttributeValue
        {
            Attribute = refAttr,
            ReferenceValue = null,
            ReferenceValueId = referencedMvoId
        };

        // Act
        JIM.Application.Servers.MetaverseServer.AddMvoChangeAttributeValueObject(change, attrValue, ValueChangeType.Remove);

        // Assert — recorded as a GUID since the navigation property isn't available
        var attrChange = change.AttributeChanges.Single();
        Assert.That(attrChange.AttributeName, Is.EqualTo("Manager"));
        var valueChange = attrChange.ValueChanges.Single();
        Assert.That(valueChange.GuidValue, Is.EqualTo(referencedMvoId));
        Assert.That(valueChange.ValueChangeType, Is.EqualTo(ValueChangeType.Remove));
    }

    [Test]
    public void AddMvoChangeAttributeValueObject_ReferenceWithNoValue_DoesNotThrowAsync()
    {
        // Arrange — reference attribute with no resolved, unresolved, or FK value
        var change = new MetaverseObjectChange();
        var refAttr = new MetaverseAttribute { Id = 10, Name = "Manager", Type = AttributeDataType.Reference };
        var attrValue = new MetaverseObjectAttributeValue
        {
            Attribute = refAttr,
            ReferenceValue = null,
            ReferenceValueId = null,
            UnresolvedReferenceValue = null
        };

        // Act & Assert — should not throw
        Assert.DoesNotThrow(() =>
            JIM.Application.Servers.MetaverseServer.AddMvoChangeAttributeValueObject(change, attrValue, ValueChangeType.Remove));

        // No value change recorded (nothing to track)
        Assert.That(change.AttributeChanges.Single().ValueChanges, Is.Empty);
    }

    #endregion
}
