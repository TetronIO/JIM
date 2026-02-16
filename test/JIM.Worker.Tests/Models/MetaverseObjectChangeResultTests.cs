using JIM.Models.Enums;
using JIM.Worker.Models;
using NUnit.Framework;

namespace JIM.Worker.Tests.Models;

/// <summary>
/// Tests for the MetaverseObjectChangeResult struct's factory methods and properties.
/// Verifies that each factory method correctly sets HasChanges, ChangeType, attribute counts,
/// and AttributeFlowCount values.
/// </summary>
[TestFixture]
public class MetaverseObjectChangeResultTests
{
    #region NoChanges Tests

    [Test]
    public void NoChanges_ReturnsResultWithHasChangesFalse()
    {
        // Act
        var result = MetaverseObjectChangeResult.NoChanges();

        // Assert
        Assert.That(result.HasChanges, Is.False);
    }

    [Test]
    public void NoChanges_ReturnsDefaultChangeType()
    {
        // Act
        var result = MetaverseObjectChangeResult.NoChanges();

        // Assert
        Assert.That(result.ChangeType, Is.EqualTo(ObjectChangeType.NotSet));
    }

    [Test]
    public void NoChanges_ReturnsZeroAttributeCounts()
    {
        // Act
        var result = MetaverseObjectChangeResult.NoChanges();

        // Assert
        Assert.That(result.AttributesAdded, Is.EqualTo(0));
        Assert.That(result.AttributesRemoved, Is.EqualTo(0));
    }

    [Test]
    public void NoChanges_ReturnsNullAttributeFlowCount()
    {
        // Act
        var result = MetaverseObjectChangeResult.NoChanges();

        // Assert
        Assert.That(result.AttributeFlowCount, Is.Null);
    }

    #endregion

    #region Projected Tests

    [Test]
    public void Projected_WithAttributesAdded_ReturnsHasChangesTrue()
    {
        // Act
        var result = MetaverseObjectChangeResult.Projected(3);

        // Assert
        Assert.That(result.HasChanges, Is.True);
    }

    [Test]
    public void Projected_WithAttributesAdded_ReturnsProjectedChangeType()
    {
        // Act
        var result = MetaverseObjectChangeResult.Projected(3);

        // Assert
        Assert.That(result.ChangeType, Is.EqualTo(ObjectChangeType.Projected));
    }

    [Test]
    public void Projected_WithAttributesAdded_ReturnsCorrectAttributesAdded()
    {
        // Act
        var result = MetaverseObjectChangeResult.Projected(5);

        // Assert
        Assert.That(result.AttributesAdded, Is.EqualTo(5));
        Assert.That(result.AttributesRemoved, Is.EqualTo(0));
    }

    [Test]
    public void Projected_WithZeroAttributes_ReturnsHasChangesTrue()
    {
        // Act - projection always has changes even with zero attributes
        var result = MetaverseObjectChangeResult.Projected(0);

        // Assert
        Assert.That(result.HasChanges, Is.True);
    }

    [Test]
    public void Projected_ReturnsNullAttributeFlowCount()
    {
        // Act
        var result = MetaverseObjectChangeResult.Projected(3);

        // Assert
        Assert.That(result.AttributeFlowCount, Is.Null);
    }

    #endregion

    #region Joined Tests

    [Test]
    public void Joined_WithDefaults_ReturnsHasChangesTrue()
    {
        // Act
        var result = MetaverseObjectChangeResult.Joined();

        // Assert
        Assert.That(result.HasChanges, Is.True);
    }

    [Test]
    public void Joined_WithDefaults_ReturnsJoinedChangeType()
    {
        // Act
        var result = MetaverseObjectChangeResult.Joined();

        // Assert
        Assert.That(result.ChangeType, Is.EqualTo(ObjectChangeType.Joined));
    }

    [Test]
    public void Joined_WithDefaults_ReturnsZeroAttributeCounts()
    {
        // Act
        var result = MetaverseObjectChangeResult.Joined();

        // Assert
        Assert.That(result.AttributesAdded, Is.EqualTo(0));
        Assert.That(result.AttributesRemoved, Is.EqualTo(0));
    }

    [Test]
    public void Joined_WithAttributeCounts_ReturnsCorrectCounts()
    {
        // Act
        var result = MetaverseObjectChangeResult.Joined(attributesAdded: 4, attributesRemoved: 2);

        // Assert
        Assert.That(result.AttributesAdded, Is.EqualTo(4));
        Assert.That(result.AttributesRemoved, Is.EqualTo(2));
    }

    [Test]
    public void Joined_ReturnsNullAttributeFlowCount()
    {
        // Act
        var result = MetaverseObjectChangeResult.Joined(attributesAdded: 3);

        // Assert
        Assert.That(result.AttributeFlowCount, Is.Null);
    }

    #endregion

    #region AttributeFlow Tests

    [Test]
    public void AttributeFlow_WithChanges_ReturnsHasChangesTrue()
    {
        // Act
        var result = MetaverseObjectChangeResult.AttributeFlow(attributesAdded: 2, attributesRemoved: 1);

        // Assert
        Assert.That(result.HasChanges, Is.True);
    }

    [Test]
    public void AttributeFlow_WithNoChanges_ReturnsHasChangesFalse()
    {
        // Act
        var result = MetaverseObjectChangeResult.AttributeFlow(attributesAdded: 0, attributesRemoved: 0);

        // Assert
        Assert.That(result.HasChanges, Is.False);
    }

    [Test]
    public void AttributeFlow_ReturnsAttributeFlowChangeType()
    {
        // Act
        var result = MetaverseObjectChangeResult.AttributeFlow(attributesAdded: 1, attributesRemoved: 0);

        // Assert
        Assert.That(result.ChangeType, Is.EqualTo(ObjectChangeType.AttributeFlow));
    }

    [Test]
    public void AttributeFlow_WithOnlyAdded_ReturnsHasChangesTrue()
    {
        // Act
        var result = MetaverseObjectChangeResult.AttributeFlow(attributesAdded: 3, attributesRemoved: 0);

        // Assert
        Assert.That(result.HasChanges, Is.True);
        Assert.That(result.AttributesAdded, Is.EqualTo(3));
        Assert.That(result.AttributesRemoved, Is.EqualTo(0));
    }

    [Test]
    public void AttributeFlow_WithOnlyRemoved_ReturnsHasChangesTrue()
    {
        // Act
        var result = MetaverseObjectChangeResult.AttributeFlow(attributesAdded: 0, attributesRemoved: 2);

        // Assert
        Assert.That(result.HasChanges, Is.True);
        Assert.That(result.AttributesAdded, Is.EqualTo(0));
        Assert.That(result.AttributesRemoved, Is.EqualTo(2));
    }

    [Test]
    public void AttributeFlow_ReturnsNullAttributeFlowCount()
    {
        // Act - AttributeFlow itself doesn't use AttributeFlowCount since it IS the attribute flow
        var result = MetaverseObjectChangeResult.AttributeFlow(attributesAdded: 2, attributesRemoved: 1);

        // Assert
        Assert.That(result.AttributeFlowCount, Is.Null);
    }

    #endregion

    #region Disconnected Tests

    [Test]
    public void Disconnected_ReturnsHasChangesTrue()
    {
        // Act
        var result = MetaverseObjectChangeResult.Disconnected();

        // Assert
        Assert.That(result.HasChanges, Is.True);
    }

    [Test]
    public void Disconnected_ReturnsDisconnectedChangeType()
    {
        // Act
        var result = MetaverseObjectChangeResult.Disconnected();

        // Assert
        Assert.That(result.ChangeType, Is.EqualTo(ObjectChangeType.Disconnected));
    }

    [Test]
    public void Disconnected_ReturnsZeroAttributeCounts()
    {
        // Act
        var result = MetaverseObjectChangeResult.Disconnected();

        // Assert
        Assert.That(result.AttributesAdded, Is.EqualTo(0));
        Assert.That(result.AttributesRemoved, Is.EqualTo(0));
    }

    [Test]
    public void Disconnected_ReturnsNullAttributeFlowCount()
    {
        // Act
        var result = MetaverseObjectChangeResult.Disconnected();

        // Assert
        Assert.That(result.AttributeFlowCount, Is.Null);
    }

    #endregion

    #region DisconnectedOutOfScope Tests

    [Test]
    public void DisconnectedOutOfScope_WithNoParameters_ReturnsHasChangesTrue()
    {
        // Act
        var result = MetaverseObjectChangeResult.DisconnectedOutOfScope();

        // Assert
        Assert.That(result.HasChanges, Is.True);
    }

    [Test]
    public void DisconnectedOutOfScope_WithNoParameters_ReturnsDisconnectedOutOfScopeChangeType()
    {
        // Act
        var result = MetaverseObjectChangeResult.DisconnectedOutOfScope();

        // Assert
        Assert.That(result.ChangeType, Is.EqualTo(ObjectChangeType.DisconnectedOutOfScope));
    }

    [Test]
    public void DisconnectedOutOfScope_WithNoParameters_ReturnsNullAttributeFlowCount()
    {
        // Act
        var result = MetaverseObjectChangeResult.DisconnectedOutOfScope();

        // Assert
        Assert.That(result.AttributeFlowCount, Is.Null);
    }

    [Test]
    public void DisconnectedOutOfScope_WithAttributeFlowCount_ReturnsCorrectCount()
    {
        // Act
        var result = MetaverseObjectChangeResult.DisconnectedOutOfScope(attributeFlowCount: 5);

        // Assert
        Assert.That(result.AttributeFlowCount, Is.EqualTo(5));
    }

    [Test]
    public void DisconnectedOutOfScope_WithZeroAttributeFlowCount_ReturnsZero()
    {
        // Act
        var result = MetaverseObjectChangeResult.DisconnectedOutOfScope(attributeFlowCount: 0);

        // Assert
        Assert.That(result.AttributeFlowCount, Is.EqualTo(0));
    }

    [Test]
    public void DisconnectedOutOfScope_WithAttributeFlowCount_ReturnsHasChangesTrue()
    {
        // Act
        var result = MetaverseObjectChangeResult.DisconnectedOutOfScope(attributeFlowCount: 3);

        // Assert
        Assert.That(result.HasChanges, Is.True);
    }

    [Test]
    public void DisconnectedOutOfScope_WithAttributeFlowCount_ReturnsDisconnectedOutOfScopeChangeType()
    {
        // Act
        var result = MetaverseObjectChangeResult.DisconnectedOutOfScope(attributeFlowCount: 3);

        // Assert
        Assert.That(result.ChangeType, Is.EqualTo(ObjectChangeType.DisconnectedOutOfScope));
    }

    [Test]
    public void DisconnectedOutOfScope_WithNullAttributeFlowCount_ReturnsNull()
    {
        // Act - explicitly passing null should behave same as no parameter
        var result = MetaverseObjectChangeResult.DisconnectedOutOfScope(attributeFlowCount: null);

        // Assert
        Assert.That(result.AttributeFlowCount, Is.Null);
    }

    [Test]
    public void DisconnectedOutOfScope_ReturnsZeroAttributeAddedAndRemoved()
    {
        // Act
        var result = MetaverseObjectChangeResult.DisconnectedOutOfScope(attributeFlowCount: 7);

        // Assert
        Assert.That(result.AttributesAdded, Is.EqualTo(0));
        Assert.That(result.AttributesRemoved, Is.EqualTo(0));
    }

    #endregion

    #region OutOfScopeRetainJoin Tests

    [Test]
    public void OutOfScopeRetainJoin_ReturnsHasChangesTrue()
    {
        // Act
        var result = MetaverseObjectChangeResult.OutOfScopeRetainJoin();

        // Assert
        Assert.That(result.HasChanges, Is.True);
    }

    [Test]
    public void OutOfScopeRetainJoin_ReturnsOutOfScopeRetainJoinChangeType()
    {
        // Act
        var result = MetaverseObjectChangeResult.OutOfScopeRetainJoin();

        // Assert
        Assert.That(result.ChangeType, Is.EqualTo(ObjectChangeType.OutOfScopeRetainJoin));
    }

    [Test]
    public void OutOfScopeRetainJoin_ReturnsZeroAttributeCounts()
    {
        // Act
        var result = MetaverseObjectChangeResult.OutOfScopeRetainJoin();

        // Assert
        Assert.That(result.AttributesAdded, Is.EqualTo(0));
        Assert.That(result.AttributesRemoved, Is.EqualTo(0));
    }

    [Test]
    public void OutOfScopeRetainJoin_ReturnsNullAttributeFlowCount()
    {
        // Act
        var result = MetaverseObjectChangeResult.OutOfScopeRetainJoin();

        // Assert
        Assert.That(result.AttributeFlowCount, Is.Null);
    }

    #endregion

    #region Default Struct Tests

    [Test]
    public void DefaultStruct_HasExpectedDefaults()
    {
        // Act
        var result = new MetaverseObjectChangeResult();

        // Assert
        Assert.That(result.HasChanges, Is.False);
        Assert.That(result.ChangeType, Is.EqualTo(ObjectChangeType.NotSet));
        Assert.That(result.AttributesAdded, Is.EqualTo(0));
        Assert.That(result.AttributesRemoved, Is.EqualTo(0));
        Assert.That(result.AttributeFlowCount, Is.Null);
    }

    #endregion

    #region AttributeFlowCount Exclusivity Tests

    [Test]
    public void OnlyDisconnectedOutOfScope_SupportsAttributeFlowCount()
    {
        // Verify that only DisconnectedOutOfScope can carry AttributeFlowCount.
        // All other factory methods should return null for AttributeFlowCount,
        // since they either ARE the attribute flow (AttributeFlow type) or don't
        // have a secondary attribute flow concern.
        var noChanges = MetaverseObjectChangeResult.NoChanges();
        var projected = MetaverseObjectChangeResult.Projected(2);
        var joined = MetaverseObjectChangeResult.Joined(1, 1);
        var attributeFlow = MetaverseObjectChangeResult.AttributeFlow(3, 1);
        var disconnected = MetaverseObjectChangeResult.Disconnected();
        var outOfScopeRetainJoin = MetaverseObjectChangeResult.OutOfScopeRetainJoin();

        Assert.That(noChanges.AttributeFlowCount, Is.Null, "NoChanges should have null AttributeFlowCount");
        Assert.That(projected.AttributeFlowCount, Is.Null, "Projected should have null AttributeFlowCount");
        Assert.That(joined.AttributeFlowCount, Is.Null, "Joined should have null AttributeFlowCount");
        Assert.That(attributeFlow.AttributeFlowCount, Is.Null, "AttributeFlow should have null AttributeFlowCount");
        Assert.That(disconnected.AttributeFlowCount, Is.Null, "Disconnected should have null AttributeFlowCount");
        Assert.That(outOfScopeRetainJoin.AttributeFlowCount, Is.Null, "OutOfScopeRetainJoin should have null AttributeFlowCount");

        // Only DisconnectedOutOfScope supports it
        var disconnectedOutOfScope = MetaverseObjectChangeResult.DisconnectedOutOfScope(attributeFlowCount: 5);
        Assert.That(disconnectedOutOfScope.AttributeFlowCount, Is.EqualTo(5), "DisconnectedOutOfScope should support AttributeFlowCount");
    }

    #endregion
}
