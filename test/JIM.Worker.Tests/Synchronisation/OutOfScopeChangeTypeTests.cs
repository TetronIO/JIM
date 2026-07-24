// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Worker.Models;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Tests for the out-of-scope change types (DisconnectedOutOfScope and OutOfScopeRetainJoin)
/// used when CSOs fall out of scope of import Synchronisation Rule scoping criteria.
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

    [Test]
    public void DisconnectedOutOfScope_WithScopingSyncRule_CarriesSyncRuleAttribution()
    {
        // Arrange
        var scopingSyncRule = new SyncRule { Id = 42, Name = "HR Import" };

        // Act
        var result = MetaverseObjectChangeResult.DisconnectedOutOfScope(scopingSyncRule: scopingSyncRule);

        // Assert
        Assert.That(result.SyncRuleId, Is.EqualTo(42),
            "The result should carry the scoping Synchronisation Rule's id for outcome attribution");
        Assert.That(result.SyncRuleName, Is.EqualTo("HR Import"),
            "The result should carry a snapshot of the scoping Synchronisation Rule's name");
    }

    [Test]
    public void DisconnectedOutOfScope_WithoutScopingSyncRule_SyncRuleAttributionIsNull()
    {
        // Act
        var result = MetaverseObjectChangeResult.DisconnectedOutOfScope();

        // Assert
        Assert.That(result.SyncRuleId, Is.Null,
            "The null case (no scoping Synchronisation Rule determinable) must remain valid");
        Assert.That(result.SyncRuleName, Is.Null);
    }

    [Test]
    public void Projected_WithProjectionSyncRule_CarriesSyncRuleAttribution()
    {
        // Arrange
        var projectionSyncRule = new SyncRule { Id = 7, Name = "HR Import" };

        // Act
        var result = MetaverseObjectChangeResult.Projected(3, projectionSyncRule);

        // Assert
        Assert.That(result.SyncRuleId, Is.EqualTo(7));
        Assert.That(result.SyncRuleName, Is.EqualTo("HR Import"));
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
