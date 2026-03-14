using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for ConnectedSystemServer change history recording (AddChangeAttributeValueObject).
/// Specifically covering edge cases in Reference attribute change recording.
/// </summary>
[TestFixture]
public class ConnectedSystemServerChangeTrackingTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepo = null!;
    private JimApplication _jim = null!;

    private ConnectedSystemObjectTypeAttribute _memberAttr = null!;
    private ConnectedSystemObjectTypeAttribute _externalIdAttr = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        _mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();

        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepo.Object);

        // Change tracking enabled (null setting → default true)
        _mockServiceSettingsRepo
            .Setup(r => r.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((ServiceSetting?)null);

        // Repository bulk insert is a no-op (change history is what we're testing)
        _mockCsRepo
            .Setup(r => r.CreateConnectedSystemObjectsAsync(It.IsAny<List<ConnectedSystemObject>>(), It.IsAny<Func<int, Task>?>()))
            .Returns(Task.CompletedTask);

        _jim = new JimApplication(_mockRepository.Object);

        _externalIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "objectGUID",
            Type = AttributeDataType.Guid,
            IsExternalId = true,
            Selected = true
        };

        _memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 2,
            Name = "member",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            Selected = true
        };
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    /// <summary>
    /// Verifies that when a group CSO is created before its member user CSOs are persisted
    /// (i.e., ReferenceValue.Id == Guid.Empty due to cross-batch ordering), the change history
    /// records the DN string via UnresolvedReferenceValue rather than storing null values.
    ///
    /// Bug: When groups are in an earlier batch than their members, ReferenceValue.Id == Guid.Empty
    /// at change history recording time. BulkInsertCsoChangeAttributeValuesRawAsync converts
    /// Guid.Empty to null, resulting in change history rows with all-null values that display
    /// as "(identifier not recorded)" in the UI.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsAsync_GroupWithUnpersistedMemberReference_RecordsUnresolvedReferenceValueInChangeHistoryAsync()
    {
        // Arrange
        var groupType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "Group",
            Attributes = new List<ConnectedSystemObjectTypeAttribute> { _externalIdAttr, _memberAttr }
        };

        // Simulate a user CSO that has been resolved in-memory but NOT yet persisted (Guid.Empty)
        var userCso = new ConnectedSystemObject
        {
            Id = Guid.Empty, // not yet persisted — batch ordering means group comes first
            ConnectedSystemId = 1,
            TypeId = 1
        };

        var memberDn = "CN=Alice Adams,OU=Users,OU=Corp,DC=sourcedomain,DC=local";

        var groupCso = new ConnectedSystemObject
        {
            Id = Guid.Empty,
            ConnectedSystemId = 1,
            TypeId = 1,
            Type = groupType,
            ExternalIdAttributeId = _externalIdAttr.Id
        };

        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = _externalIdAttr,
            AttributeId = _externalIdAttr.Id,
            GuidValue = Guid.NewGuid(),
            ConnectedSystemObject = groupCso
        });

        // The member attribute value: resolved in-memory (ReferenceValue set) but Id == Guid.Empty,
        // and UnresolvedReferenceValue still holds the original DN string
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = _memberAttr,
            AttributeId = _memberAttr.Id,
            ReferenceValue = userCso,           // resolved to in-memory CSO (Id == Guid.Empty)
            UnresolvedReferenceValue = memberDn, // original DN preserved (not cleared during resolution)
            ConnectedSystemObject = groupCso
        });

        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = groupCso
        };

        // Act
        await _jim.ConnectedSystems.CreateConnectedSystemObjectsAsync(
            new List<ConnectedSystemObject> { groupCso },
            new List<ActivityRunProfileExecutionItem> { rpei });

        // Assert: change history should record the DN string for the member attribute,
        // not an all-null value that displays as "(identifier not recorded)"
        Assert.That(rpei.ConnectedSystemObjectChange, Is.Not.Null,
            "Change record should be created for the group CSO");

        var memberAttributeChange = rpei.ConnectedSystemObjectChange!.AttributeChanges
            .SingleOrDefault(ac => ac.Attribute.Name == "member");
        Assert.That(memberAttributeChange, Is.Not.Null,
            "Change record should include the member attribute");

        Assert.That(memberAttributeChange!.ValueChanges, Has.Count.EqualTo(1),
            "Should have exactly one member value change");

        var valueChange = memberAttributeChange.ValueChanges.Single();
        Assert.That(valueChange.StringValue, Is.EqualTo(memberDn),
            "Member change history should record the DN string via UnresolvedReferenceValue, " +
            "not null (which would display as '(identifier not recorded)' in the UI)");
        Assert.That(valueChange.ReferenceValue, Is.Null,
            "ReferenceValue should be null since the CSO has Guid.Empty Id (not yet persisted)");
    }

    /// <summary>
    /// Verifies that when a group CSO is created and its member user CSOs are already persisted
    /// (ReferenceValue.Id != Guid.Empty), the change history records the CSO reference correctly.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsAsync_GroupWithPersistedMemberReference_RecordsCsoReferenceInChangeHistoryAsync()
    {
        // Arrange
        var groupType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "Group",
            Attributes = new List<ConnectedSystemObjectTypeAttribute> { _externalIdAttr, _memberAttr }
        };

        // Simulate a user CSO that is already persisted (has a real Id)
        var userCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), // already persisted — has a real ID
            ConnectedSystemId = 1,
            TypeId = 1
        };

        var memberDn = "CN=Bob Brown,OU=Users,OU=Corp,DC=sourcedomain,DC=local";

        var groupCso = new ConnectedSystemObject
        {
            Id = Guid.Empty,
            ConnectedSystemId = 1,
            TypeId = 1,
            Type = groupType,
            ExternalIdAttributeId = _externalIdAttr.Id
        };

        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = _externalIdAttr,
            AttributeId = _externalIdAttr.Id,
            GuidValue = Guid.NewGuid(),
            ConnectedSystemObject = groupCso
        });

        // The member attribute value: resolved to a persisted CSO (Id != Guid.Empty)
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = _memberAttr,
            AttributeId = _memberAttr.Id,
            ReferenceValue = userCso,           // resolved to persisted CSO (Id != Guid.Empty)
            ReferenceValueId = userCso.Id,      // FK already set
            UnresolvedReferenceValue = memberDn, // original DN preserved
            ConnectedSystemObject = groupCso
        });

        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = groupCso
        };

        // Act
        await _jim.ConnectedSystems.CreateConnectedSystemObjectsAsync(
            new List<ConnectedSystemObject> { groupCso },
            new List<ActivityRunProfileExecutionItem> { rpei });

        // Assert: change history should record the CSO reference (not the DN string)
        Assert.That(rpei.ConnectedSystemObjectChange, Is.Not.Null,
            "Change record should be created for the group CSO");

        var memberAttributeChange = rpei.ConnectedSystemObjectChange!.AttributeChanges
            .SingleOrDefault(ac => ac.Attribute.Name == "member");
        Assert.That(memberAttributeChange, Is.Not.Null,
            "Change record should include the member attribute");

        Assert.That(memberAttributeChange!.ValueChanges, Has.Count.EqualTo(1),
            "Should have exactly one member value change");

        var valueChange = memberAttributeChange.ValueChanges.Single();
        Assert.That(valueChange.ReferenceValue, Is.EqualTo(userCso),
            "Member change history should record the CSO reference when the CSO is already persisted");
        Assert.That(valueChange.StringValue, Is.Null,
            "StringValue should be null when a proper CSO reference is recorded");
    }
}
