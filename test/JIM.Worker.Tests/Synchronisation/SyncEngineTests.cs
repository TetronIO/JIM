using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Pure unit tests for <see cref="SyncEngine"/>.
/// No mocking, no database, no EF Core — plain C# objects only.
/// </summary>
public class SyncEngineTests
{
    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new SyncEngine();
    }

    #region EvaluateProjection

    [Test]
    public void EvaluateProjection_NoProjectionRule_ReturnsNoProjectionAsync()
    {
        var cso = CreateCso(typeId: 1);
        var syncRules = new List<SyncRule>
        {
            CreateImportSyncRule(csoTypeId: 1, projectToMetaverse: false)
        };

        var result = _engine.EvaluateProjection(cso, syncRules);

        Assert.That(result.ShouldProject, Is.False);
        Assert.That(result.MetaverseObjectType, Is.Null);
    }

    [Test]
    public void EvaluateProjection_WithProjectionRule_ReturnsProjectAsync()
    {
        var mvoType = new MetaverseObjectType { Id = 10, Name = "Person" };
        var cso = CreateCso(typeId: 1);
        var syncRules = new List<SyncRule>
        {
            CreateImportSyncRule(csoTypeId: 1, projectToMetaverse: true, mvoType: mvoType)
        };

        var result = _engine.EvaluateProjection(cso, syncRules);

        Assert.That(result.ShouldProject, Is.True);
        Assert.That(result.MetaverseObjectType, Is.SameAs(mvoType));
    }

    [Test]
    public void EvaluateProjection_WrongObjectType_ReturnsNoProjectionAsync()
    {
        var cso = CreateCso(typeId: 1);
        var syncRules = new List<SyncRule>
        {
            CreateImportSyncRule(csoTypeId: 2, projectToMetaverse: true)
        };

        var result = _engine.EvaluateProjection(cso, syncRules);

        Assert.That(result.ShouldProject, Is.False);
    }

    #endregion

    #region EvaluateMvoDeletionRule

    [Test]
    public void EvaluateMvoDeletionRule_ManualRule_ReturnsNotDeletedAsync()
    {
        var mvo = CreateMvo(deletionRule: MetaverseObjectDeletionRule.Manual);

        var result = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 0);

        Assert.That(result.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    [Test]
    public void EvaluateMvoDeletionRule_InternalOrigin_ReturnsNotDeletedAsync()
    {
        var mvo = CreateMvo(deletionRule: MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);
        mvo.Origin = MetaverseObjectOrigin.Internal;

        var result = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 0);

        Assert.That(result.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    [Test]
    public void EvaluateMvoDeletionRule_WhenLastDisconnected_RemainingConnectors_ReturnsNotDeletedAsync()
    {
        var mvo = CreateMvo(deletionRule: MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);

        var result = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 2);

        Assert.That(result.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    [Test]
    public void EvaluateMvoDeletionRule_WhenLastDisconnected_NoRemaining_NoGrace_ReturnsDeleteImmediatelyAsync()
    {
        var mvo = CreateMvo(
            deletionRule: MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: null);

        var result = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 0);

        Assert.That(result.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
    }

    [Test]
    public void EvaluateMvoDeletionRule_WhenLastDisconnected_NoRemaining_WithGrace_ReturnsDeletionScheduledAsync()
    {
        var mvo = CreateMvo(
            deletionRule: MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.FromDays(30));

        var result = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 0);

        Assert.That(result.Fate, Is.EqualTo(MvoDeletionFate.DeletionScheduled));
        Assert.That(result.GracePeriod, Is.EqualTo(TimeSpan.FromDays(30)));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AuthoritativeSource_Disconnected_ReturnsDeleteImmediatelyAsync()
    {
        var mvo = CreateMvo(
            deletionRule: MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: null,
            triggerSystemIds: [1, 2]);

        var result = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 5);

        Assert.That(result.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
    }

    [Test]
    public void EvaluateMvoDeletionRule_NonAuthoritativeSource_Disconnected_ReturnsNotDeletedAsync()
    {
        var mvo = CreateMvo(
            deletionRule: MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            triggerSystemIds: [1, 2]);

        var result = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 99, remainingCsoCount: 3);

        Assert.That(result.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    [Test]
    public void EvaluateMvoDeletionRule_NoType_ReturnsNotDeletedAsync()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };

        var result = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 0);

        Assert.That(result.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    #endregion

    #region ApplyPendingAttributeChanges

    [Test]
    public void ApplyPendingAttributeChanges_AddsAndRemovesAsync()
    {
        var mvo = CreateMvo();
        var existingAttr = new MetaverseObjectAttributeValue { StringValue = "old" };
        var newAttr = new MetaverseObjectAttributeValue { StringValue = "new" };
        mvo.AttributeValues.Add(existingAttr);
        mvo.PendingAttributeValueRemovals.Add(existingAttr);
        mvo.PendingAttributeValueAdditions.Add(newAttr);

        _engine.ApplyPendingAttributeChanges(mvo);

        Assert.That(mvo.AttributeValues, Does.Not.Contain(existingAttr));
        Assert.That(mvo.AttributeValues, Does.Contain(newAttr));
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
    }

    [Test]
    public void ApplyPendingAttributeChanges_NoPendingChanges_DoesNothingAsync()
    {
        var mvo = CreateMvo();
        var existingAttr = new MetaverseObjectAttributeValue { StringValue = "keep" };
        mvo.AttributeValues.Add(existingAttr);

        _engine.ApplyPendingAttributeChanges(mvo);

        Assert.That(mvo.AttributeValues, Has.Count.EqualTo(1));
        Assert.That(mvo.AttributeValues.First().StringValue, Is.EqualTo("keep"));
    }

    #endregion

    #region DetermineOutOfScopeAction

    [Test]
    public void DetermineOutOfScopeAction_NoImportRule_ReturnsDisconnectAsync()
    {
        var cso = CreateCso(typeId: 1);
        var syncRules = new List<SyncRule>
        {
            new() { Direction = SyncRuleDirection.Export, Enabled = true, ConnectedSystemObjectTypeId = 1 }
        };

        var result = _engine.DetermineOutOfScopeAction(cso, syncRules);

        Assert.That(result, Is.EqualTo(InboundOutOfScopeAction.Disconnect));
    }

    [Test]
    public void DetermineOutOfScopeAction_ImportRuleWithRemainJoined_ReturnsRemainJoinedAsync()
    {
        var cso = CreateCso(typeId: 1);
        var syncRules = new List<SyncRule>
        {
            new()
            {
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystemObjectTypeId = 1,
                InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined
            }
        };

        var result = _engine.DetermineOutOfScopeAction(cso, syncRules);

        Assert.That(result, Is.EqualTo(InboundOutOfScopeAction.RemainJoined));
    }

    #endregion

    #region AttributeValuesMatch

    [Test]
    public void AttributeValuesMatch_StringMatch_ReturnsTrueAsync()
    {
        var csoValue = new ConnectedSystemObjectAttributeValue { StringValue = "hello" };
        var pendingChange = new PendingExportAttributeValueChange { StringValue = "hello" };

        Assert.That(_engine.AttributeValuesMatch(csoValue, pendingChange), Is.True);
    }

    [Test]
    public void AttributeValuesMatch_StringMismatch_ReturnsFalseAsync()
    {
        var csoValue = new ConnectedSystemObjectAttributeValue { StringValue = "hello" };
        var pendingChange = new PendingExportAttributeValueChange { StringValue = "world" };

        Assert.That(_engine.AttributeValuesMatch(csoValue, pendingChange), Is.False);
    }

    [Test]
    public void AttributeValuesMatch_IntMatch_ReturnsTrueAsync()
    {
        var csoValue = new ConnectedSystemObjectAttributeValue { IntValue = 42 };
        var pendingChange = new PendingExportAttributeValueChange { IntValue = 42 };

        Assert.That(_engine.AttributeValuesMatch(csoValue, pendingChange), Is.True);
    }

    [Test]
    public void AttributeValuesMatch_DateTimeMatch_ReturnsTrueAsync()
    {
        var dt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var csoValue = new ConnectedSystemObjectAttributeValue { DateTimeValue = dt };
        var pendingChange = new PendingExportAttributeValueChange { DateTimeValue = dt };

        Assert.That(_engine.AttributeValuesMatch(csoValue, pendingChange), Is.True);
    }

    [Test]
    public void AttributeValuesMatch_NullPendingValues_ReturnsTrueAsync()
    {
        var csoValue = new ConnectedSystemObjectAttributeValue { StringValue = "anything" };
        var pendingChange = new PendingExportAttributeValueChange(); // all nulls

        Assert.That(_engine.AttributeValuesMatch(csoValue, pendingChange), Is.True);
    }

    #endregion

    #region EvaluatePendingExportConfirmation

    [Test]
    public void EvaluatePendingExportConfirmation_NoPendingExports_ReturnsNoneAsync()
    {
        var cso = CreateCso();
        var result = _engine.EvaluatePendingExportConfirmation(cso, null);

        Assert.That(result.HasResults, Is.False);
    }

    [Test]
    public void EvaluatePendingExportConfirmation_AllChangesConfirmed_ReturnsDeleteAsync()
    {
        var cso = CreateCso();
        var attrId = 10;
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = attrId, StringValue = "expected" });

        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.ExportNotConfirmed,
            ChangeType = PendingExportChangeType.Update,
            AttributeValueChanges = [new PendingExportAttributeValueChange
            {
                AttributeId = attrId,
                ChangeType = PendingExportAttributeChangeType.Update,
                StringValue = "expected"
            }]
        };

        var pendingExports = new Dictionary<Guid, List<PendingExport>>
        {
            { cso.Id, [pe] }
        };

        var result = _engine.EvaluatePendingExportConfirmation(cso, pendingExports);

        Assert.That(result.ToDelete, Has.Count.EqualTo(1));
        Assert.That(result.ToUpdate, Has.Count.EqualTo(0));
    }

    [Test]
    public void EvaluatePendingExportConfirmation_NoChangesConfirmed_ReturnsUpdateAsync()
    {
        var cso = CreateCso();
        var attrId = 10;
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = attrId, StringValue = "different" });

        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.ExportNotConfirmed,
            ChangeType = PendingExportChangeType.Update,
            AttributeValueChanges = [new PendingExportAttributeValueChange
            {
                AttributeId = attrId,
                ChangeType = PendingExportAttributeChangeType.Update,
                StringValue = "expected"
            }]
        };

        var pendingExports = new Dictionary<Guid, List<PendingExport>>
        {
            { cso.Id, [pe] }
        };

        var result = _engine.EvaluatePendingExportConfirmation(cso, pendingExports);

        Assert.That(result.ToDelete, Has.Count.EqualTo(0));
        Assert.That(result.ToUpdate, Has.Count.EqualTo(1));
        Assert.That(pe.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed));
        Assert.That(pe.ErrorCount, Is.EqualTo(1));
    }

    [Test]
    public void EvaluatePendingExportConfirmation_PendingStatus_SkippedAsync()
    {
        var cso = CreateCso();
        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.Pending,
            AttributeValueChanges = [new PendingExportAttributeValueChange
            {
                AttributeId = 10,
                ChangeType = PendingExportAttributeChangeType.Update,
                StringValue = "expected"
            }]
        };

        var pendingExports = new Dictionary<Guid, List<PendingExport>>
        {
            { cso.Id, [pe] }
        };

        var result = _engine.EvaluatePendingExportConfirmation(cso, pendingExports);

        Assert.That(result.HasResults, Is.False);
    }

    [Test]
    public void EvaluatePendingExportConfirmation_PartialSuccess_ChangesToUpdate_RemovesConfirmedChangesAsync()
    {
        var cso = CreateCso();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 10, StringValue = "good" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 20, StringValue = "wrong" });

        var confirmedChange = new PendingExportAttributeValueChange
        {
            AttributeId = 10, ChangeType = PendingExportAttributeChangeType.Update, StringValue = "good"
        };
        var failedChange = new PendingExportAttributeValueChange
        {
            AttributeId = 20, ChangeType = PendingExportAttributeChangeType.Update, StringValue = "expected"
        };

        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.ExportNotConfirmed,
            ChangeType = PendingExportChangeType.Create,
            AttributeValueChanges = [confirmedChange, failedChange]
        };

        var pendingExports = new Dictionary<Guid, List<PendingExport>>
        {
            { cso.Id, [pe] }
        };

        var result = _engine.EvaluatePendingExportConfirmation(cso, pendingExports);

        Assert.That(result.ToUpdate, Has.Count.EqualTo(1));
        Assert.That(pe.AttributeValueChanges, Has.Count.EqualTo(1));
        Assert.That(pe.AttributeValueChanges[0], Is.SameAs(failedChange));
        Assert.That(pe.ChangeType, Is.EqualTo(PendingExportChangeType.Update), "Create should be changed to Update on partial success");
    }

    #endregion

    #region FlowInboundAttributes

    [Test]
    public void FlowInboundAttributes_SimpleTextFlow_AddsValueToMvoAsync()
    {
        var mvoAttr = new MetaverseAttribute { Id = 100, Name = "DisplayName", Type = AttributeDataType.Text };
        var csotAttr = new ConnectedSystemObjectTypeAttribute { Id = 200, Name = "displayName", Type = AttributeDataType.Text };
        var csoType = new ConnectedSystemObjectType
        {
            Id = 1,
            Attributes = [csotAttr]
        };

        var mvo = CreateMvo();
        var cso = CreateCso(typeId: 1);
        cso.MetaverseObject = mvo;
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 200,
            StringValue = "John Smith"
        });

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttr };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = 200, Order = 1 });
        var syncRule = new SyncRule
        {
            Direction = SyncRuleDirection.Import,
            AttributeFlowRules = [mapping]
        };

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions[0].StringValue, Is.EqualTo("John Smith"));
        Assert.That(mvo.PendingAttributeValueAdditions[0].AttributeId, Is.EqualTo(100));
    }

    [Test]
    public void FlowInboundAttributes_NoCsoMetaverseObject_DoesNothingAsync()
    {
        var cso = CreateCso(typeId: 1);
        // cso.MetaverseObject is null

        var syncRule = new SyncRule
        {
            Direction = SyncRuleDirection.Import,
            AttributeFlowRules = []
        };

        // Should not throw
        _engine.FlowInboundAttributes(cso, syncRule, []);
    }

    #endregion

    #region Helpers

    private static ConnectedSystemObject CreateCso(int typeId = 1)
    {
        return new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            TypeId = typeId,
            ConnectedSystemId = 1,
            Status = ConnectedSystemObjectStatus.Normal,
            AttributeValues = [],
            Type = new ConnectedSystemObjectType { Id = typeId }
        };
    }

    private static MetaverseObject CreateMvo(
        MetaverseObjectDeletionRule deletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
        TimeSpan? gracePeriod = null,
        List<int>? triggerSystemIds = null)
    {
        return new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Origin = MetaverseObjectOrigin.Projected,
            Type = new MetaverseObjectType
            {
                Id = 1,
                Name = "Person",
                DeletionRule = deletionRule,
                DeletionGracePeriod = gracePeriod,
                DeletionTriggerConnectedSystemIds = triggerSystemIds ?? []
            }
        };
    }

    private static SyncRule CreateImportSyncRule(
        int csoTypeId = 1,
        bool projectToMetaverse = false,
        MetaverseObjectType? mvoType = null)
    {
        return new SyncRule
        {
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemObjectTypeId = csoTypeId,
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = csoTypeId },
            ProjectToMetaverse = projectToMetaverse,
            MetaverseObjectType = mvoType ?? new MetaverseObjectType { Id = 1, Name = "Person" }
        };
    }

    #endregion
}
