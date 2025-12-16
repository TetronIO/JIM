using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

[TestFixture]
public class SyncRuleMappingProcessorTests
{
    private MetaverseObjectType _metaverseObjectType = null!;
    private MetaverseAttribute _targetAttribute = null!;
    private ConnectedSystemObjectType _connectedSystemObjectType = null!;
    private ConnectedSystemObjectTypeAttribute _sourceAttribute = null!;

    [SetUp]
    public void SetUp()
    {
        // Set up common test data
        _metaverseObjectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User"
        };

        _targetAttribute = new MetaverseAttribute
        {
            Id = 1,
            Name = "Display Name",
            Type = AttributeDataType.Text
        };

        _connectedSystemObjectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "SOURCE_USER"
        };

        _sourceAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "firstName",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = _connectedSystemObjectType
        };
    }

    [Test]
    public void Process_ExpressionMapping_StringConcatenation_AppliesCorrectly()
    {
        // Arrange
        var cso = CreateConnectedSystemObject();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = 2,
                Name = "firstName",
                Type = AttributeDataType.Text
            },
            AttributeId = 2,
            StringValue = "John"
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = 3,
                Name = "lastName",
                Type = AttributeDataType.Text
            },
            AttributeId = 3,
            StringValue = "Doe"
        });

        var syncRuleMapping = new SyncRuleMapping
        {
            Id = 1,
            TargetMetaverseAttribute = _targetAttribute,
            TargetMetaverseAttributeId = _targetAttribute.Id
        };

        syncRuleMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1,
            Order = 0,
            Expression = "cs[\"firstName\"] + \" \" + cs[\"lastName\"]"
        });

        var objectTypes = new List<ConnectedSystemObjectType> { _connectedSystemObjectType };

        // Act
        SyncRuleMappingProcessor.Process(cso, syncRuleMapping, objectTypes);

        // Assert
        Assert.That(cso.MetaverseObject!.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        var addedValue = cso.MetaverseObject.PendingAttributeValueAdditions[0];
        Assert.That(addedValue.StringValue, Is.EqualTo("John Doe"));
        Assert.That(addedValue.AttributeId, Is.EqualTo(_targetAttribute.Id));
    }

    [Test]
    public void Process_ExpressionMapping_WithBuiltInFunction_AppliesCorrectly()
    {
        // Arrange
        var cso = CreateConnectedSystemObject();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = 2,
                Name = "email",
                Type = AttributeDataType.Text
            },
            AttributeId = 2,
            StringValue = "  JOHN.DOE@EXAMPLE.COM  "
        });

        var syncRuleMapping = new SyncRuleMapping
        {
            Id = 1,
            TargetMetaverseAttribute = _targetAttribute,
            TargetMetaverseAttributeId = _targetAttribute.Id
        };

        syncRuleMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1,
            Order = 0,
            Expression = "Lower(Trim(cs[\"email\"]))"
        });

        var objectTypes = new List<ConnectedSystemObjectType> { _connectedSystemObjectType };

        // Act
        SyncRuleMappingProcessor.Process(cso, syncRuleMapping, objectTypes);

        // Assert
        Assert.That(cso.MetaverseObject!.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        var addedValue = cso.MetaverseObject.PendingAttributeValueAdditions[0];
        Assert.That(addedValue.StringValue, Is.EqualTo("john.doe@example.com"));
    }

    [Test]
    public void Process_ExpressionMapping_WithDNEscaping_AppliesCorrectly()
    {
        // Arrange
        var cso = CreateConnectedSystemObject();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = 2,
                Name = "displayName",
                Type = AttributeDataType.Text
            },
            AttributeId = 2,
            StringValue = "Doe, John"
        });

        var targetDnAttribute = new MetaverseAttribute
        {
            Id = 2,
            Name = "DistinguishedName",
            Type = AttributeDataType.Text
        };

        var syncRuleMapping = new SyncRuleMapping
        {
            Id = 1,
            TargetMetaverseAttribute = targetDnAttribute,
            TargetMetaverseAttributeId = targetDnAttribute.Id
        };

        syncRuleMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1,
            Order = 0,
            Expression = "\"CN=\" + EscapeDN(cs[\"displayName\"]) + \",OU=Users,DC=corp,DC=local\""
        });

        var objectTypes = new List<ConnectedSystemObjectType> { _connectedSystemObjectType };

        // Act
        SyncRuleMappingProcessor.Process(cso, syncRuleMapping, objectTypes);

        // Assert
        Assert.That(cso.MetaverseObject!.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        var addedValue = cso.MetaverseObject.PendingAttributeValueAdditions[0];
        Assert.That(addedValue.StringValue, Is.EqualTo("CN=Doe\\, John,OU=Users,DC=corp,DC=local"));
    }

    [Test]
    public void Process_ExpressionMapping_ConditionalExpression_AppliesCorrectly()
    {
        // Arrange
        var cso = CreateConnectedSystemObject();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = 2,
                Name = "accountName",
                Type = AttributeDataType.Text
            },
            AttributeId = 2,
            StringValue = "jdoe"
        });

        // Add a department attribute to the MVO
        var departmentAttr = new MetaverseAttribute
        {
            Id = 3,
            Name = "Department",
            Type = AttributeDataType.Text
        };
        cso.MetaverseObject!.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            AttributeId = 3,
            Attribute = departmentAttr,
            StringValue = "IT"
        });

        var syncRuleMapping = new SyncRuleMapping
        {
            Id = 1,
            TargetMetaverseAttribute = _targetAttribute,
            TargetMetaverseAttributeId = _targetAttribute.Id
        };

        syncRuleMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1,
            Order = 0,
            Expression = "mv[\"Department\"] == \"IT\" ? \"tech-\" + cs[\"accountName\"] : cs[\"accountName\"]"
        });

        var objectTypes = new List<ConnectedSystemObjectType> { _connectedSystemObjectType };

        // Act
        SyncRuleMappingProcessor.Process(cso, syncRuleMapping, objectTypes);

        // Assert
        Assert.That(cso.MetaverseObject!.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        var addedValue = cso.MetaverseObject.PendingAttributeValueAdditions[0];
        Assert.That(addedValue.StringValue, Is.EqualTo("tech-jdoe"));
    }

    [Test]
    public void Process_ExpressionMapping_NumberResult_AppliesCorrectly()
    {
        // Arrange
        var cso = CreateConnectedSystemObject();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = 2,
                Name = "age",
                Type = AttributeDataType.Text
            },
            AttributeId = 2,
            StringValue = "42"
        });

        var targetNumberAttribute = new MetaverseAttribute
        {
            Id = 4,
            Name = "Age",
            Type = AttributeDataType.Number
        };

        var syncRuleMapping = new SyncRuleMapping
        {
            Id = 1,
            TargetMetaverseAttribute = targetNumberAttribute,
            TargetMetaverseAttributeId = targetNumberAttribute.Id
        };

        syncRuleMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1,
            Order = 0,
            Expression = "ToInt(cs[\"age\"])"
        });

        var objectTypes = new List<ConnectedSystemObjectType> { _connectedSystemObjectType };

        // Act
        SyncRuleMappingProcessor.Process(cso, syncRuleMapping, objectTypes);

        // Assert
        Assert.That(cso.MetaverseObject!.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        var addedValue = cso.MetaverseObject.PendingAttributeValueAdditions[0];
        Assert.That(addedValue.IntValue, Is.EqualTo(42));
    }

    [Test]
    public void Process_ExpressionMapping_NullResult_RemovesExistingValue()
    {
        // Arrange
        var cso = CreateConnectedSystemObject();

        // Add an existing value to the MVO
        cso.MetaverseObject!.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            AttributeId = _targetAttribute.Id,
            Attribute = _targetAttribute,
            StringValue = "Existing Value"
        });

        var syncRuleMapping = new SyncRuleMapping
        {
            Id = 1,
            TargetMetaverseAttribute = _targetAttribute,
            TargetMetaverseAttributeId = _targetAttribute.Id
        };

        syncRuleMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1,
            Order = 0,
            Expression = "cs[\"nonExistentAttribute\"]"  // This will return null
        });

        var objectTypes = new List<ConnectedSystemObjectType> { _connectedSystemObjectType };

        // Act
        SyncRuleMappingProcessor.Process(cso, syncRuleMapping, objectTypes);

        // Assert
        Assert.That(cso.MetaverseObject!.PendingAttributeValueRemovals.Count, Is.EqualTo(1));
        Assert.That(cso.MetaverseObject!.PendingAttributeValueAdditions.Count, Is.EqualTo(0));
    }

    [Test]
    public void Process_ExpressionMapping_UpdateExistingValue_RemovesOldAndAddsNew()
    {
        // Arrange
        var cso = CreateConnectedSystemObject();

        // Add an existing value to the MVO
        var existingValue = new MetaverseObjectAttributeValue
        {
            AttributeId = _targetAttribute.Id,
            Attribute = _targetAttribute,
            StringValue = "Old Value"
        };
        cso.MetaverseObject!.AttributeValues.Add(existingValue);

        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = 2,
                Name = "name",
                Type = AttributeDataType.Text
            },
            AttributeId = 2,
            StringValue = "New Value"
        });

        var syncRuleMapping = new SyncRuleMapping
        {
            Id = 1,
            TargetMetaverseAttribute = _targetAttribute,
            TargetMetaverseAttributeId = _targetAttribute.Id
        };

        syncRuleMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1,
            Order = 0,
            Expression = "cs[\"name\"]"
        });

        var objectTypes = new List<ConnectedSystemObjectType> { _connectedSystemObjectType };

        // Act
        SyncRuleMappingProcessor.Process(cso, syncRuleMapping, objectTypes);

        // Assert
        Assert.That(cso.MetaverseObject!.PendingAttributeValueRemovals.Count, Is.EqualTo(1));
        Assert.That(cso.MetaverseObject!.PendingAttributeValueRemovals[0], Is.EqualTo(existingValue));
        Assert.That(cso.MetaverseObject!.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(cso.MetaverseObject!.PendingAttributeValueAdditions[0].StringValue, Is.EqualTo("New Value"));
    }

    [Test]
    public void Process_ExpressionMapping_SameValue_DoesNotUpdate()
    {
        // Arrange
        var cso = CreateConnectedSystemObject();

        // Add an existing value to the MVO that matches what the expression will produce
        cso.MetaverseObject!.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            AttributeId = _targetAttribute.Id,
            Attribute = _targetAttribute,
            StringValue = "Same Value"
        });

        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = 2,
                Name = "name",
                Type = AttributeDataType.Text
            },
            AttributeId = 2,
            StringValue = "Same Value"
        });

        var syncRuleMapping = new SyncRuleMapping
        {
            Id = 1,
            TargetMetaverseAttribute = _targetAttribute,
            TargetMetaverseAttributeId = _targetAttribute.Id
        };

        syncRuleMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 1,
            Order = 0,
            Expression = "cs[\"name\"]"
        });

        var objectTypes = new List<ConnectedSystemObjectType> { _connectedSystemObjectType };

        // Act
        SyncRuleMappingProcessor.Process(cso, syncRuleMapping, objectTypes);

        // Assert
        Assert.That(cso.MetaverseObject!.PendingAttributeValueRemovals.Count, Is.EqualTo(0));
        Assert.That(cso.MetaverseObject!.PendingAttributeValueAdditions.Count, Is.EqualTo(0));
    }

    private ConnectedSystemObject CreateConnectedSystemObject()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = _metaverseObjectType,
            TypeId = _metaverseObjectType.Id,
            AttributeValues = new List<MetaverseObjectAttributeValue>(),
            PendingAttributeValueAdditions = new List<MetaverseObjectAttributeValue>(),
            PendingAttributeValueRemovals = new List<MetaverseObjectAttributeValue>()
        };

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            TypeId = _connectedSystemObjectType.Id,
            MetaverseObject = mvo,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };

        return cso;
    }
}
