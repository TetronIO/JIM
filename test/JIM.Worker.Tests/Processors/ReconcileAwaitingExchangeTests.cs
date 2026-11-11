// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.InMemoryData;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exchange;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// The confirming import's side of the Exchange hold (#2021), through <c>SyncImportTaskProcessor</c>: reconciliation
/// looks up which objects have a change on the Exchange queue only when a page holds a change awaiting Exchange, hands
/// the answer to the engine, and raises no "not confirmed" item for a held change; once the object has no queued change
/// the change is judged and reported as any other; and a hold on an object the import did not return is judged after
/// reconciliation, so it is never stranded. Invokes the private steps by reflection, as
/// <see cref="ReconcileAwaitingInitialPasswordTests"/> does, so the fixture drives production code.
/// </summary>
[TestFixture]
public class ReconcileAwaitingExchangeTests
{
    private const int ConnectedSystemId = 1;
    private const int ObjectTypeId = 100;
    private const int RecipientTypeAttributeId = 40;

    [TestCase(PendingExchangeChangeStatus.Pending)]
    [TestCase(PendingExchangeChangeStatus.Parked)]
    [TestCase(PendingExchangeChangeStatus.Refused)]
    public async Task ReconcilePendingExports_ChangeAwaitingExchangeWithAQueuedChange_HoldsItWithNoItemAsync(PendingExchangeChangeStatus queuedStatus)
    {
        var fixture = BuildFixture(awaitsExchange: true, importedRecipientType: ExchangeRecipientTypes.None);
        await fixture.Repository.StageExchangeChangesAsync([Queued(fixture.Cso, queuedStatus)]);

        await InvokeReconcileAsync(fixture);

        var change = fixture.Repository.PendingExports[fixture.PendingExportId].AttributeValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(change.AwaitsExchange, Is.True);
            Assert.That(ExecutionItems(fixture.Processor), Is.Empty, "Exchange has not acted yet: a hold raises no item at all");
            Assert.That(fixture.Repository.GetConnectedSystemObjectIdsWithExchangeChangeCallCount, Is.EqualTo(1),
                "one lookup for the page that holds a change awaiting Exchange");
        }
    }

    [Test]
    public async Task ReconcilePendingExports_ChangeAwaitingExchangeWithNoQueuedChange_ReportsItNotConfirmedAndClearsTheFlagAsync()
    {
        var fixture = BuildFixture(awaitsExchange: true, importedRecipientType: ExchangeRecipientTypes.None);

        await InvokeReconcileAsync(fixture);

        var pendingExport = fixture.Repository.PendingExports[fixture.PendingExportId];
        var change = pendingExport.AttributeValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));
            Assert.That(change.AwaitsExchange, Is.False);
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed));
            Assert.That(ExecutionItems(fixture.Processor).Count(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed),
                Is.EqualTo(1), "Exchange has nothing left to do and the import does not show the change: an ordinary unconfirmed export");
        }
    }

    [Test]
    public async Task ReconcilePendingExports_NothingAwaitingExchange_NeverLooksUpTheQueueAsync()
    {
        var fixture = BuildFixture(awaitsExchange: false, importedRecipientType: ExchangeRecipientTypes.None);

        await InvokeReconcileAsync(fixture);

        Assert.That(fixture.Repository.GetConnectedSystemObjectIdsWithExchangeChangeCallCount, Is.Zero,
            "a Connected System that never queues an Exchange change must not pay for the lookup");
    }

    [Test]
    public async Task JudgeUnreconciledExchangeHolds_HoldWhoseQueuedChangeIsGoneAndNotShown_ReleasesItToBeSentAgainAsync()
    {
        var fixture = BuildFixture(awaitsExchange: true, importedRecipientType: ExchangeRecipientTypes.None);

        await InvokeJudgeUnreconciledAsync(fixture);

        var pendingExport = fixture.Repository.PendingExports[fixture.PendingExportId];
        var change = pendingExport.AttributeValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));
            Assert.That(change.AwaitsExchange, Is.False);
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed), "so the next export sends it again");
        }
    }

    [Test]
    public async Task JudgeUnreconciledExchangeHolds_HoldWhoseQueuedChangeIsGoneAndShown_ConfirmsItAsync()
    {
        var fixture = BuildFixture(awaitsExchange: true, importedRecipientType: ExchangeRecipientTypes.Mailbox);

        await InvokeJudgeUnreconciledAsync(fixture);

        Assert.That(fixture.Repository.PendingExports.ContainsKey(fixture.PendingExportId), Is.False,
            "its only change is confirmed, so the Pending Export is done");
    }

    [Test]
    public async Task JudgeUnreconciledExchangeHolds_HoldWithAQueuedChange_LeavesItAsync()
    {
        var fixture = BuildFixture(awaitsExchange: true, importedRecipientType: ExchangeRecipientTypes.None);
        await fixture.Repository.StageExchangeChangesAsync([Queued(fixture.Cso, PendingExchangeChangeStatus.Pending)]);

        await InvokeJudgeUnreconciledAsync(fixture);

        var change = fixture.Repository.PendingExports[fixture.PendingExportId].AttributeValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(change.AwaitsExchange, Is.True);
        }
    }

    [Test]
    public async Task JudgeUnreconciledExchangeHolds_ObjectThisImportReconciled_IsNotJudgedAgainAsync()
    {
        var fixture = BuildFixture(awaitsExchange: true, importedRecipientType: ExchangeRecipientTypes.None);

        await InvokeJudgeUnreconciledAsync(fixture, reconciled: [fixture.Cso]);

        var change = fixture.Repository.PendingExports[fixture.PendingExportId].AttributeValueChanges.Single();
        Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
            "reconciliation has already judged what this import returned");
    }

    [Test]
    public async Task ReconcilePendingExports_ChangeQueuedWhenTheImportStartedAndDeliveredSince_StillHoldsItAsync()
    {
        // The race the AD lab found (Scenario 034): Exchange delivered the change after the import read the object, so
        // the import shows the old values, but before reconciliation asked the queue, which no longer has it. What the
        // import read predates Exchange acting, so the change is held for the next import, not judged against it.
        var fixture = BuildFixture(awaitsExchange: true, importedRecipientType: ExchangeRecipientTypes.None);
        await fixture.Repository.StageExchangeChangesAsync([Queued(fixture.Cso, PendingExchangeChangeStatus.Pending)]);
        await InvokeRecordHoldsAtImportStartAsync(fixture);
        await DeliverEveryQueuedChangeAsync(fixture.Repository);

        await InvokeReconcileAsync(fixture);

        var change = fixture.Repository.PendingExports[fixture.PendingExportId].AttributeValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Repository.PendingExchangeChanges, Is.Empty, "the fixture delivered the change mid-import");
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(change.AwaitsExchange, Is.True);
            Assert.That(ExecutionItems(fixture.Processor), Is.Empty);
        }
    }

    [Test]
    public async Task JudgeUnreconciledExchangeHolds_ChangeQueuedWhenTheImportStartedAndDeliveredSince_LeavesItAsync()
    {
        var fixture = BuildFixture(awaitsExchange: true, importedRecipientType: ExchangeRecipientTypes.None);
        await fixture.Repository.StageExchangeChangesAsync([Queued(fixture.Cso, PendingExchangeChangeStatus.Pending)]);
        await InvokeRecordHoldsAtImportStartAsync(fixture);
        await DeliverEveryQueuedChangeAsync(fixture.Repository);

        await InvokeJudgeUnreconciledAsync(fixture);

        var change = fixture.Repository.PendingExports[fixture.PendingExportId].AttributeValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(change.AwaitsExchange, Is.True, "the next import judges it against what Exchange stamped");
        }
    }

    private static async Task DeliverEveryQueuedChangeAsync(SyncRepository repository)
    {
        // As the Exchange delivery lane does: claim, act, delete.
        const string lane = "exchange-lane";
        var claimed = await repository.ClaimDueExchangeChangesAsync(ConnectedSystemId, lane, DateTime.UtcNow.AddMinutes(1), TimeSpan.FromMinutes(5), 100);
        await repository.DeleteExchangeChangesAsync(claimed.Select(c => c.Id), lane);
    }

    private static async Task InvokeRecordHoldsAtImportStartAsync(Fixture fixture)
    {
        const string methodName = "RecordHoldsStandingAtImportStartAsync";
        var method = typeof(SyncImportTaskProcessor).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{methodName} was not found. Update this fixture to invoke the current production method.");

        await (Task)method.Invoke(fixture.Processor, [])!;
    }

    private static PendingExchangeChange Queued(ConnectedSystemObject cso, PendingExchangeChangeStatus status) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = ConnectedSystemId,
        ConnectedSystemObjectId = cso.Id,
        MetaverseObjectId = cso.MetaverseObjectId,
        RecipientType = ExchangeRecipientTypes.Mailbox,
        Status = status,
        CreatedAt = DateTime.UtcNow,
        ActivityId = Guid.NewGuid()
    };

    private static Task InvokeReconcileAsync(Fixture fixture) =>
        ImportReconciliationInvoker.ReconcileAsync(fixture.Processor, [fixture.Cso]);

    private static async Task InvokeJudgeUnreconciledAsync(Fixture fixture, List<ConnectedSystemObject>? reconciled = null)
    {
        const string methodName = "JudgeUnreconciledExchangeHoldsAsync";
        var method = typeof(SyncImportTaskProcessor).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{methodName} was not found. Update this fixture to invoke the current production method.");

        await (Task)method.Invoke(fixture.Processor, [reconciled ?? new List<ConnectedSystemObject>()])!;
    }

    /// <summary>
    /// Every execution item the run raised: those it has recorded (written with their batch since #2908) and those it
    /// still holds.
    /// </summary>
    private static List<ActivityRunProfileExecutionItem> ExecutionItems(SyncImportTaskProcessor processor)
    {
        static object Field(SyncImportTaskProcessor processor, string fieldName) =>
            typeof(SyncImportTaskProcessor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(processor)
            ?? throw new InvalidOperationException($"SyncImportTaskProcessor.{fieldName} was not found. Update this fixture if it has been renamed.");

        var recorded = ((Activity)Field(processor, "_activity")).RunProfileExecutionItems;
        var held = (List<ActivityRunProfileExecutionItem>)Field(processor, "_activityRunProfileExecutionItems");
        return [.. recorded, .. held];
    }

    private sealed record Fixture(SyncImportTaskProcessor Processor, SyncRepository Repository, ConnectedSystemObject Cso, Guid PendingExportId);

    /// <summary>
    /// A joined user whose import read the given recipient type, and its Pending Export declaring a mailbox, flagged or
    /// not as awaiting Exchange.
    /// </summary>
    private static Fixture BuildFixture(bool awaitsExchange, string importedRecipientType)
    {
        var repository = new SyncRepository();
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = RecipientTypeAttributeId, Name = ExchangeRecipientAttributes.RecipientType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsConnectorDefined = true
        };

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            TypeId = ObjectTypeId,
            MetaverseObjectId = Guid.NewGuid(),
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow,
            AttributeValues =
            [
                new ConnectedSystemObjectAttributeValue
                {
                    Id = Guid.NewGuid(), AttributeId = RecipientTypeAttributeId, Attribute = attribute, StringValue = importedRecipientType
                }
            ]
        };
        repository.SeedConnectedSystemObject(cso);

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectId = cso.Id,
            ConnectedSystemObject = cso,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Exported,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    AttributeId = RecipientTypeAttributeId,
                    Attribute = attribute,
                    ChangeType = PendingExportAttributeChangeType.Update,
                    Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
                    ExportAttemptCount = 1,
                    StringValue = ExchangeRecipientTypes.Mailbox,
                    AwaitsExchange = awaitsExchange
                }
            ]
        };
        repository.SeedPendingExport(pendingExport);

        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Panoply",
            ObjectTypes =
            [
                new ConnectedSystemObjectType
                {
                    Id = ObjectTypeId, Name = "user", ConnectedSystemId = ConnectedSystemId, Selected = true, Attributes = [attribute]
                }
            ]
        };
        var runProfile = new ConnectedSystemRunProfile
        {
            Name = "Delta Import", RunType = ConnectedSystemRunType.DeltaImport, ConnectedSystemId = ConnectedSystemId
        };

        var syncServer = new Mock<ISyncServer>();
        syncServer.Setup(s => s.GetSyncPageSizeAsync()).ReturnsAsync(500);

        var processor = new SyncImportTaskProcessor(
            null!,
            repository,
            syncServer.Object,
            new SyncEngine(),
            new MockFileConnector(),
            connectedSystem,
            runProfile,
            TestUtilities.CreateTestWorkerTask(new Activity(), initiatedBy: null),
            new CancellationTokenSource());

        return new Fixture(processor, repository, cso, pendingExport.Id);
    }
}
