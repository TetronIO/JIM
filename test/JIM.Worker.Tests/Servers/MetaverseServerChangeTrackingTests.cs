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
    private JimApplication _jim = null!;

    private MetaverseObjectType _userType = null!;
    private MetaverseAttribute _displayNameAttr = null!;
    private MetaverseAttribute _departmentAttr = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();

        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepo.Object);

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

    [Test]
    public async Task CreateMetaverseObjectChangeAsync_WithCreatedChangeType_CreatesCorrectChangeRecordAsync()
    {
        // Arrange
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        var additions = new List<MetaverseObjectAttributeValue>
        {
            new() { Attribute = _displayNameAttr, StringValue = "Alice Adams" },
            new() { Attribute = _departmentAttr, StringValue = "Engineering" }
        };
        var removals = new List<MetaverseObjectAttributeValue>();
        var userId = Guid.NewGuid();

        // Act
        await _jim.Metaverse.CreateMetaverseObjectChangeAsync(
            mvo,
            additions,
            removals,
            ActivityInitiatorType.User,
            userId,
            "Test User",
            ObjectChangeType.Created,
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
    public async Task CreateMetaverseObjectChangeAsync_WithCreatedChangeType_AllAttributeValuesAreAdditionsAsync()
    {
        // Arrange
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        var additions = new List<MetaverseObjectAttributeValue>
        {
            new() { Attribute = _displayNameAttr, StringValue = "Bob Brown" },
            new() { Attribute = _departmentAttr, StringValue = "Finance" }
        };

        // Act
        await _jim.Metaverse.CreateMetaverseObjectChangeAsync(
            mvo,
            additions,
            new List<MetaverseObjectAttributeValue>(),
            changeType: ObjectChangeType.Created,
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
    public async Task CreateMetaverseObjectChangeAsync_WhenChangeTrackingDisabled_DoesNotCreateChangeRecordAsync()
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
        var additions = new List<MetaverseObjectAttributeValue>
        {
            new() { Attribute = _displayNameAttr, StringValue = "Charlie Clark" }
        };

        // Act
        await _jim.Metaverse.CreateMetaverseObjectChangeAsync(
            mvo,
            additions,
            new List<MetaverseObjectAttributeValue>(),
            changeType: ObjectChangeType.Created,
            changeInitiatorType: MetaverseObjectChangeInitiatorType.DataGeneration);

        // Assert
        Assert.That(mvo.Changes, Has.Count.EqualTo(0));
    }

    [Test]
    public async Task CreateMetaverseObjectChangeAsync_DefaultParameters_UsesUpdatedChangeTypeAsync()
    {
        // Arrange - verify backwards compatibility: default parameters preserve existing behaviour
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

        // Act - call without the new optional parameters
        await _jim.Metaverse.CreateMetaverseObjectChangeAsync(
            mvo,
            additions,
            removals,
            ActivityInitiatorType.User,
            userId,
            "Test User");

        // Assert - should use Updated and derive User initiator type
        var change = mvo.Changes[0];
        Assert.That(change.ChangeType, Is.EqualTo(ObjectChangeType.Updated));
        Assert.That(change.ChangeInitiatorType, Is.EqualTo(MetaverseObjectChangeInitiatorType.User));
    }

    [Test]
    public async Task CreateMetaverseObjectChangeAsync_WithNoAdditionsOrRemovals_DoesNotCreateChangeRecordAsync()
    {
        // Arrange
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };

        // Act
        await _jim.Metaverse.CreateMetaverseObjectChangeAsync(
            mvo,
            new List<MetaverseObjectAttributeValue>(),
            new List<MetaverseObjectAttributeValue>(),
            changeType: ObjectChangeType.Created,
            changeInitiatorType: MetaverseObjectChangeInitiatorType.DataGeneration);

        // Assert
        Assert.That(mvo.Changes, Has.Count.EqualTo(0));
    }
}
