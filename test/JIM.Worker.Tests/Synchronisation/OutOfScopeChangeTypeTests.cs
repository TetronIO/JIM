using JIM.Models.Enums;
using JIM.Worker.Models;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Tests for the out-of-scope change types (DisconnectedOutOfScope and OutOfScopeRetainJoin)
/// used when CSOs fall out of scope of import sync rule scoping criteria.
/// </summary>
[TestFixture]
public class OutOfScopeChangeTypeTests
{
    #region MetaverseObjectChangeResult Factory Method Tests

    [Test]
    public void DisconnectedOutOfScope_ReturnsResultWithCorrectChangeType()
    {
        // Act
        var result = MetaverseObjectChangeResult.DisconnectedOutOfScope();

        // Assert
        Assert.That(result.HasChanges, Is.True);
        Assert.That(result.ChangeType, Is.EqualTo(ObjectChangeType.DisconnectedOutOfScope));
    }

    [Test]
    public void OutOfScopeRetainJoin_ReturnsResultWithCorrectChangeType()
    {
        // Act
        var result = MetaverseObjectChangeResult.OutOfScopeRetainJoin();

        // Assert
        Assert.That(result.HasChanges, Is.True);
        Assert.That(result.ChangeType, Is.EqualTo(ObjectChangeType.OutOfScopeRetainJoin));
    }

    [Test]
    public void DisconnectedOutOfScope_IsDifferentFromRegularDisconnected()
    {
        // Act
        var disconnectedOutOfScope = MetaverseObjectChangeResult.DisconnectedOutOfScope();
        var regularDisconnected = MetaverseObjectChangeResult.Disconnected();

        // Assert
        Assert.That(disconnectedOutOfScope.ChangeType, Is.Not.EqualTo(regularDisconnected.ChangeType),
            "DisconnectedOutOfScope should be distinct from regular Disconnected for audit trail clarity");
    }

    [Test]
    public void OutOfScopeRetainJoin_HasChangesIsTrue()
    {
        // The OutOfScopeRetainJoin result should indicate that something meaningful happened
        // (the CSO fell out of scope) even though the join was preserved.
        // This ensures an RPEI is created for audit purposes.

        // Act
        var result = MetaverseObjectChangeResult.OutOfScopeRetainJoin();

        // Assert
        Assert.That(result.HasChanges, Is.True,
            "OutOfScopeRetainJoin should have HasChanges=true to ensure RPEI creation for audit trail");
    }

    #endregion

    #region ObjectChangeType Enum Value Tests

    [Test]
    public void ObjectChangeType_DisconnectedOutOfScope_Exists()
    {
        // Verify the enum value exists and is distinct
        var changeType = ObjectChangeType.DisconnectedOutOfScope;

        Assert.That(changeType, Is.Not.EqualTo(ObjectChangeType.NotSet));
        Assert.That(changeType, Is.Not.EqualTo(ObjectChangeType.Disconnected));
    }

    [Test]
    public void ObjectChangeType_OutOfScopeRetainJoin_Exists()
    {
        // Verify the enum value exists and is distinct
        var changeType = ObjectChangeType.OutOfScopeRetainJoin;

        Assert.That(changeType, Is.Not.EqualTo(ObjectChangeType.NotSet));
        Assert.That(changeType, Is.Not.EqualTo(ObjectChangeType.Disconnected));
        Assert.That(changeType, Is.Not.EqualTo(ObjectChangeType.DisconnectedOutOfScope));
    }

    #endregion
}
