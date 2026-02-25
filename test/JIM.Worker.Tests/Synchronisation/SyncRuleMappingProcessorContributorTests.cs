using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Tests for SyncRuleMappingProcessor ContributedBySystemId tracking.
/// Verifies that MVO attribute values created during sync flow correctly record
/// which connected system contributed them.
/// </summary>
public class SyncRuleMappingProcessorContributorTests
{
    private const int ConnectedSystemId = 42;
    private ConnectedSystemObjectType _csoType = null!;
    private MetaverseObjectType _mvoType = null!;
    private MetaverseAttribute _textAttribute = null!;
    private MetaverseAttribute _numberAttribute = null!;
    private MetaverseAttribute _dateTimeAttribute = null!;
    private MetaverseAttribute _boolAttribute = null!;
    private MetaverseAttribute _guidAttribute = null!;
    private MetaverseAttribute _referenceAttribute = null!;
    private ConnectedSystemObjectTypeAttribute _csTextAttribute = null!;
    private ConnectedSystemObjectTypeAttribute _csNumberAttribute = null!;
    private ConnectedSystemObjectTypeAttribute _csDateTimeAttribute = null!;
    private ConnectedSystemObjectTypeAttribute _csBoolAttribute = null!;
    private ConnectedSystemObjectTypeAttribute _csGuidAttribute = null!;
    private ConnectedSystemObjectTypeAttribute _csReferenceAttribute = null!;

    [SetUp]
    public void Setup()
    {
        _csTextAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "displayName", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        _csNumberAttribute = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "employeeNumber", Type = AttributeDataType.Number, AttributePlurality = AttributePlurality.SingleValued };
        _csDateTimeAttribute = new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "startDate", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.SingleValued };
        _csBoolAttribute = new ConnectedSystemObjectTypeAttribute { Id = 4, Name = "isActive", Type = AttributeDataType.Boolean, AttributePlurality = AttributePlurality.SingleValued };
        _csGuidAttribute = new ConnectedSystemObjectTypeAttribute { Id = 5, Name = "objectGuid", Type = AttributeDataType.Guid, IsExternalId = true, AttributePlurality = AttributePlurality.SingleValued };
        _csReferenceAttribute = new ConnectedSystemObjectTypeAttribute { Id = 6, Name = "manager", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued };

        _csoType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "User",
            Attributes = new List<ConnectedSystemObjectTypeAttribute>
            {
                _csTextAttribute, _csNumberAttribute, _csDateTimeAttribute,
                _csBoolAttribute, _csGuidAttribute, _csReferenceAttribute
            }
        };

        _mvoType = new MetaverseObjectType { Id = 1, Name = "user" };

        _textAttribute = new MetaverseAttribute { Id = 10, Name = "displayName", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        _numberAttribute = new MetaverseAttribute { Id = 11, Name = "employeeNumber", Type = AttributeDataType.Number, AttributePlurality = AttributePlurality.SingleValued };
        _dateTimeAttribute = new MetaverseAttribute { Id = 12, Name = "startDate", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.SingleValued };
        _boolAttribute = new MetaverseAttribute { Id = 13, Name = "isActive", Type = AttributeDataType.Boolean, AttributePlurality = AttributePlurality.SingleValued };
        _guidAttribute = new MetaverseAttribute { Id = 14, Name = "objectGuid", Type = AttributeDataType.Guid, AttributePlurality = AttributePlurality.SingleValued };
        _referenceAttribute = new MetaverseAttribute { Id = 15, Name = "manager", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued };
    }

    private (ConnectedSystemObject cso, MetaverseObject mvo) CreateCsoAndMvo()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = _mvoType,
            AttributeValues = new List<MetaverseObjectAttributeValue>()
        };

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = _csoType,
            TypeId = _csoType.Id,
            ConnectedSystemId = ConnectedSystemId,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            ExternalIdAttributeId = _csGuidAttribute.Id,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };

        return (cso, mvo);
    }

    private SyncRuleMapping CreateMapping(ConnectedSystemObjectTypeAttribute source, MetaverseAttribute target)
    {
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = target };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Order = 1,
            ConnectedSystemAttributeId = source.Id,
            ConnectedSystemAttribute = source
        });
        return mapping;
    }

    [Test]
    public void Process_TextAttribute_SetsContributedBySystemIdAsync()
    {
        var (cso, mvo) = CreateCsoAndMvo();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = _csTextAttribute.Id,
            Attribute = _csTextAttribute,
            StringValue = "Joe Bloggs"
        });

        var mapping = CreateMapping(_csTextAttribute, _textAttribute);
        SyncRuleMappingProcessor.Process(cso, mapping, new List<ConnectedSystemObjectType> { _csoType },
            contributingSystemId: ConnectedSystemId);

        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions[0].ContributedBySystemId, Is.EqualTo(ConnectedSystemId));
        Assert.That(mvo.PendingAttributeValueAdditions[0].StringValue, Is.EqualTo("Joe Bloggs"));
    }

    [Test]
    public void Process_NumberAttribute_SetsContributedBySystemIdAsync()
    {
        var (cso, mvo) = CreateCsoAndMvo();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = _csNumberAttribute.Id,
            Attribute = _csNumberAttribute,
            IntValue = 12345
        });

        var mapping = CreateMapping(_csNumberAttribute, _numberAttribute);
        SyncRuleMappingProcessor.Process(cso, mapping, new List<ConnectedSystemObjectType> { _csoType },
            contributingSystemId: ConnectedSystemId);

        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions[0].ContributedBySystemId, Is.EqualTo(ConnectedSystemId));
        Assert.That(mvo.PendingAttributeValueAdditions[0].IntValue, Is.EqualTo(12345));
    }

    [Test]
    public void Process_DateTimeAttribute_SetsContributedBySystemIdAsync()
    {
        var (cso, mvo) = CreateCsoAndMvo();
        var testDate = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = _csDateTimeAttribute.Id,
            Attribute = _csDateTimeAttribute,
            DateTimeValue = testDate
        });

        var mapping = CreateMapping(_csDateTimeAttribute, _dateTimeAttribute);
        SyncRuleMappingProcessor.Process(cso, mapping, new List<ConnectedSystemObjectType> { _csoType },
            contributingSystemId: ConnectedSystemId);

        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions[0].ContributedBySystemId, Is.EqualTo(ConnectedSystemId));
        Assert.That(mvo.PendingAttributeValueAdditions[0].DateTimeValue, Is.EqualTo(testDate));
    }

    [Test]
    public void Process_BooleanAttribute_SetsContributedBySystemIdAsync()
    {
        var (cso, mvo) = CreateCsoAndMvo();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = _csBoolAttribute.Id,
            Attribute = _csBoolAttribute,
            BoolValue = true
        });

        var mapping = CreateMapping(_csBoolAttribute, _boolAttribute);
        SyncRuleMappingProcessor.Process(cso, mapping, new List<ConnectedSystemObjectType> { _csoType },
            contributingSystemId: ConnectedSystemId);

        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions[0].ContributedBySystemId, Is.EqualTo(ConnectedSystemId));
        Assert.That(mvo.PendingAttributeValueAdditions[0].BoolValue, Is.True);
    }

    [Test]
    public void Process_GuidAttribute_SetsContributedBySystemIdAsync()
    {
        var (cso, mvo) = CreateCsoAndMvo();
        var testGuid = Guid.NewGuid();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = _csGuidAttribute.Id,
            Attribute = _csGuidAttribute,
            GuidValue = testGuid
        });

        var mapping = CreateMapping(_csGuidAttribute, _guidAttribute);
        SyncRuleMappingProcessor.Process(cso, mapping, new List<ConnectedSystemObjectType> { _csoType },
            contributingSystemId: ConnectedSystemId);

        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions[0].ContributedBySystemId, Is.EqualTo(ConnectedSystemId));
        Assert.That(mvo.PendingAttributeValueAdditions[0].GuidValue, Is.EqualTo(testGuid));
    }

    [Test]
    public void Process_ReferenceAttribute_SetsContributedBySystemIdAsync()
    {
        var (cso, mvo) = CreateCsoAndMvo();

        // Create a referenced MVO (the manager)
        var managerMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _mvoType };
        var managerCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = _csoType,
            TypeId = _csoType.Id,
            MetaverseObject = managerMvo,
            MetaverseObjectId = managerMvo.Id
        };

        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = _csReferenceAttribute.Id,
            Attribute = _csReferenceAttribute,
            ReferenceValue = managerCso,
            ReferenceValueId = managerCso.Id
        });

        var mapping = CreateMapping(_csReferenceAttribute, _referenceAttribute);
        SyncRuleMappingProcessor.Process(cso, mapping, new List<ConnectedSystemObjectType> { _csoType },
            contributingSystemId: ConnectedSystemId);

        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions[0].ContributedBySystemId, Is.EqualTo(ConnectedSystemId));
    }

    [Test]
    public void Process_WithNullContributingSystemId_LeavesContributedBySystemIdNullAsync()
    {
        var (cso, mvo) = CreateCsoAndMvo();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = _csTextAttribute.Id,
            Attribute = _csTextAttribute,
            StringValue = "Internally managed"
        });

        var mapping = CreateMapping(_csTextAttribute, _textAttribute);
        // Pass null for contributingSystemId (future: internally-managed MVO attributes)
        SyncRuleMappingProcessor.Process(cso, mapping, new List<ConnectedSystemObjectType> { _csoType },
            contributingSystemId: null);

        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions[0].ContributedBySystemId, Is.Null);
    }

    [Test]
    public void Process_DateTimeAttributeUpdate_SetsContributedBySystemIdOnReplacementAsync()
    {
        var (cso, mvo) = CreateCsoAndMvo();
        var oldDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newDate = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        // Pre-populate MVO with an existing value
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = _dateTimeAttribute,
            AttributeId = _dateTimeAttribute.Id,
            DateTimeValue = oldDate,
            ContributedBySystemId = 99 // contributed by a different system originally
        });

        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = _csDateTimeAttribute.Id,
            Attribute = _csDateTimeAttribute,
            DateTimeValue = newDate
        });

        var mapping = CreateMapping(_csDateTimeAttribute, _dateTimeAttribute);
        SyncRuleMappingProcessor.Process(cso, mapping, new List<ConnectedSystemObjectType> { _csoType },
            contributingSystemId: ConnectedSystemId);

        // Old value should be marked for removal, new value should have updated contributor
        Assert.That(mvo.PendingAttributeValueRemovals, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions[0].ContributedBySystemId, Is.EqualTo(ConnectedSystemId));
        Assert.That(mvo.PendingAttributeValueAdditions[0].DateTimeValue, Is.EqualTo(newDate));
    }
}
