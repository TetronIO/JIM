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
            MetaverseObjectChangeInitiatorType.DataGeneration);

        // Assert
        Assert.That(mvo.Changes, Has.Count.EqualTo(1));
        var change = mvo.Changes[0];
        Assert.That(change.ChangeType, Is.EqualTo(ObjectChangeType.Created));
        Assert.That(change.ChangeInitiatorType, Is.EqualTo(MetaverseObjectChangeInitiatorType.DataGeneration));
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
            changeInitiatorType: MetaverseObjectChangeInitiatorType.DataGeneration);

        // Assert
        var change = mvo.Changes[0];
        foreach (var attrChange in change.AttributeChanges)
        {
            Assert.That(attrChange.ValueChanges, Has.Count.EqualTo(1));
            Assert.That(attrChange.ValueChanges[0].ValueChangeType, Is.EqualTo(ValueChangeType.Add));
        }

        var displayNameChange = change.AttributeChanges.Single(ac => ac.Attribute.Id == _displayNameAttr.Id);
        Assert.That(displayNameChange.ValueChanges[0].StringValue, Is.EqualTo("Bob Brown"));

        var departmentChange = change.AttributeChanges.Single(ac => ac.Attribute.Id == _departmentAttr.Id);
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
            changeInitiatorType: MetaverseObjectChangeInitiatorType.DataGeneration);

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
}
