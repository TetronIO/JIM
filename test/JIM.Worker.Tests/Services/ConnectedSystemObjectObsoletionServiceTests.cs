// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Sync;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Direct coverage of the extracted per-object obsoletion core (#809 Phase 1). The behaviour itself is
/// proven by the full obsoletion/recall/deletion-rule suites running unchanged through the worker's
/// adapter; these tests pin the SEAM: the core's outputs are data, in the shape the future #134/#827
/// read-only preview adapter will consume.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectObsoletionServiceTests
{
    private const int SystemId = 11;

    private Mock<ISyncRepository> _syncRepository = null!;
    private Mock<IExpressionEvaluator> _expressionEvaluator = null!;
    private SyncEngine _syncEngine = null!;

    private MetaverseObject _mvo = null!;
    private ConnectedSystemObject _cso = null!;
    private MetaverseObjectAttributeValue _contributedValue = null!;

    [SetUp]
    public void SetUp()
    {
        _syncRepository = new Mock<ISyncRepository>();
        _expressionEvaluator = new Mock<IExpressionEvaluator>();
        _syncEngine = new SyncEngine();

        var mvoType = new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People" };
        var displayName = new MetaverseAttribute { Id = 100, Name = "Display Name", Type = AttributeDataType.Text };

        _mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType };
        _contributedValue = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = displayName,
            AttributeId = displayName.Id,
            StringValue = "Alice",
            ContributedBySystemId = SystemId,
            MetaverseObject = _mvo
        };
        _mvo.AttributeValues.Add(_contributedValue);

        var csoType = new ConnectedSystemObjectType
        {
            Id = 2,
            Name = "user",
            ConnectedSystemId = SystemId,
            RemoveContributedAttributesOnObsoletion = true
        };
        _cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = SystemId,
            Type = csoType,
            TypeId = csoType.Id,
            Status = ConnectedSystemObjectStatus.Obsolete,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            DateJoined = DateTime.UtcNow,
            MetaverseObject = _mvo,
            MetaverseObjectId = _mvo.Id
        };
        _mvo.ConnectedSystemObjects.Add(_cso);

        _syncRepository
            .Setup(r => r.GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync(_mvo.Id))
            .ReturnsAsync([SystemId]);
    }

    private Task<ConnectedSystemObjectObsoletionResult> ProcessAsync(
        ConnectedSystemObject cso,
        Func<MetaverseObject, int, IReadOnlyCollection<int>, Task<(MvoDeletionDecision, string?)>>? processMvoDeletionRuleAsync = null,
        Action<MetaverseObject>? recordPreRecallAttributeSnapshot = null)
    {
        return ConnectedSystemObjectObsoletionService.ProcessObsoleteConnectedSystemObjectAsync(
            cso,
            activeSyncRules: [],
            ContributorRecallScope.ForObsoletingConnectedSystemObject(cso),
            priorityContext: null,
            _syncEngine,
            _syncRepository.Object,
            isCsoInScopeForImportRule: (_, _) => true,
            objectTypes: [cso.Type],
            _expressionEvaluator.Object,
            executionItemFactory: () => new ActivityRunProfileExecutionItem(),
            ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed,
            processMvoDeletionRuleAsync ?? ((_, _, _) =>
                Task.FromResult((new MvoDeletionDecision { Fate = MvoDeletionFate.NotDeleted }, (string?)null))),
            recordPreRecallAttributeSnapshot ?? (_ => { }));
    }

    [Test]
    public async Task ProcessObsoleteCso_SoleContributorRecalled_StagesOutputsAsDataAsync()
    {
        // Arrange: capture what the deletion-rule delegate is asked, and the pre-recall snapshot moment.
        IReadOnlyCollection<int>? remainingIdsSeen = null;
        List<Guid>? snapshotValueIdsAtCapture = null;

        // Act
        var result = await ProcessAsync(
            _cso,
            processMvoDeletionRuleAsync: (mvo, disconnectingSystemId, remainingIds) =>
            {
                Assert.That(disconnectingSystemId, Is.EqualTo(SystemId));
                remainingIdsSeen = remainingIds;
                return Task.FromResult((new MvoDeletionDecision { Fate = MvoDeletionFate.NotDeleted }, (string?)null));
            },
            recordPreRecallAttributeSnapshot: mvo => snapshotValueIdsAtCapture = mvo.AttributeValues.Select(av => av.Id).ToList());

        // Assert: the operation's outputs are data, in the shape the processor adapter (and the future
        // read-only preview adapter) consumes.
        using (Assert.EnterMultipleScope())
        {
            // One RPEI recording the disconnection (deletion is a child outcome on the same item).
            Assert.That(result.ExecutionItems, Has.Count.EqualTo(1));
            Assert.That(result.ExecutionItems[0].ObjectChangeType, Is.EqualTo(ObjectChangeType.Disconnected));
            Assert.That(result.ExecutionItems[0].AttributeFlowCount, Is.EqualTo(1));

            // The CSO is staged for deletion with its execution item; nothing is deleted quietly.
            Assert.That(result.CsoDeletions, Has.Count.EqualTo(1));
            Assert.That(result.CsoDeletions[0].Cso, Is.SameAs(_cso));
            Assert.That(result.CsoDeletions[0].ExecutionItem, Is.SameAs(result.ExecutionItems[0]));
            Assert.That(result.QuietCsoDeletions, Is.Empty);

            // The sole contributor's value is recalled: a removal with no re-elected replacement.
            Assert.That(result.MvoAttributeChange, Is.Not.Null);
            var mvoAttributeChange = result.MvoAttributeChange!.Value;
            Assert.That(mvoAttributeChange.Removals, Is.EqualTo(new[] { _contributedValue }));
            Assert.That(mvoAttributeChange.Additions, Is.Empty);
            Assert.That(mvoAttributeChange.ChangeType, Is.EqualTo(ObjectChangeType.Disconnected));
            Assert.That(result.RecallClearedAttributeCount, Is.EqualTo(1));

            // The Metaverse Object is staged for persistence and export evaluation, and the recall was applied.
            Assert.That(result.MvoToUpdate, Is.SameAs(_mvo));
            Assert.That(result.ExportEvaluation, Is.Not.Null);
            var exportEvaluation = result.ExportEvaluation!.Value;
            Assert.That(exportEvaluation.ChangedAttributes, Is.EqualTo(new[] { _contributedValue }));
            Assert.That(exportEvaluation.RemovedAttributes, Is.EqualTo(new[] { _contributedValue }));
            Assert.That(_mvo.AttributeValues, Is.Empty, "the recalled value must have been applied (removed) by the core");

            // The join is broken and reported.
            Assert.That(result.DisconnectedMetaverseObjectId, Is.EqualTo(_mvo.Id));
            Assert.That(_cso.MetaverseObject, Is.Null);
            Assert.That(_cso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.NotJoined));
            Assert.That(_mvo.ConnectedSystemObjects, Is.Empty);

            // The deletion-rule verdict flows through as data, evaluated with the leaver excluded from
            // the remaining-system list, and the pre-recall snapshot was captured while the value stood.
            Assert.That(result.MvoDeletionDecision?.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
            Assert.That(remainingIdsSeen, Is.Empty);
            Assert.That(snapshotValueIdsAtCapture, Is.EqualTo(new[] { _contributedValue.Id }));
        }
    }

    [Test]
    public async Task ProcessObsoleteCso_NotObsolete_StagesNothingAsync()
    {
        _cso.Status = ConnectedSystemObjectStatus.Normal;

        var result = await ProcessAsync(_cso);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ExecutionItems, Is.Empty);
            Assert.That(result.CsoDeletions, Is.Empty);
            Assert.That(result.QuietCsoDeletions, Is.Empty);
            Assert.That(_cso.MetaverseObject, Is.SameAs(_mvo), "a non-obsolete object must be untouched");
        }
    }

    [Test]
    public async Task ProcessObsoleteCso_PreDisconnected_IsDeletedQuietlyAsync()
    {
        _cso.MetaverseObject = null;
        _cso.MetaverseObjectId = null;
        _cso.JoinType = ConnectedSystemObjectJoinType.NotJoined;

        var result = await ProcessAsync(_cso);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.QuietCsoDeletions, Is.EqualTo(new[] { _cso }),
                "a pre-disconnected object's disconnection was already recorded, so it is deleted without an RPEI");
            Assert.That(result.ExecutionItems, Is.Empty);
            Assert.That(result.CsoDeletions, Is.Empty);
        }
    }
}
