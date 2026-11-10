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
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// The confirming import's side of #2039 Phase 2, through <c>SyncImportTaskProcessor</c>'s Pending Export reconciliation:
/// it looks up which accounts are still owed a password only when a page holds a change written with an interim
/// value, hands the answer to the engine, raises no "not confirmed" item for a held change, and persists a release.
/// Invokes the private step by reflection, as <see cref="UnconfirmedExportedCreateRetryTests"/> does, so the fixture
/// drives production code.
/// </summary>
[TestFixture]
public class ReconcileAwaitingInitialPasswordTests
{
    private const int ConnectedSystemId = 1;
    private const int ObjectTypeId = 100;
    private const int UserAccountControlAttributeId = 30;
    private const int Enabled = 512;
    private const int Disabled = 514;

    [Test]
    public async Task ReconcilePendingExports_AwaitingChangeWithPasswordOwed_HoldsItWithNoNotConfirmedItemAsync()
    {
        var fixture = BuildFixture(awaitsInitialPassword: true);
        await fixture.Repository.QueuePasswordChangesAsync([OwedPassword(fixture.Cso, PendingPasswordChangeStatus.Pending)]);

        await InvokeReconcileAsync(fixture);

        var change = fixture.Repository.PendingExports[fixture.PendingExportId].AttributeValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(change.AwaitsInitialPassword, Is.True);
            Assert.That(ExecutionItems(fixture.Processor).Where(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed),
                Is.Empty, "the interim value was written on purpose: a hold is not an unconfirmed export");
            Assert.That(ExecutionItems(fixture.Processor), Is.Empty, "a hold raises no item at all");
            Assert.That(fixture.Repository.GetMetaverseObjectIdsOwedPasswordCallCount, Is.EqualTo(1),
                "one lookup for the page that holds a flagged change");
        }
    }

    [Test]
    public async Task ReconcilePendingExports_AwaitingChangeWithAParkedPassword_HoldsItAsync()
    {
        // Parked waits on a person (a Retry or a rule edit); the password is still owed, so the account stays held.
        var fixture = BuildFixture(awaitsInitialPassword: true);
        await fixture.Repository.QueuePasswordChangesAsync([OwedPassword(fixture.Cso, PendingPasswordChangeStatus.Parked)]);

        await InvokeReconcileAsync(fixture);

        var change = fixture.Repository.PendingExports[fixture.PendingExportId].AttributeValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(change.AwaitsInitialPassword, Is.True);
        }
    }

    [Test]
    public async Task ReconcilePendingExports_AwaitingChangeWithAnExpiredPassword_ReleasesItWithNoItemAsync()
    {
        // An expired password will never arrive, so nothing is owed: the change goes back to Pending and the next
        // export writes the rule's value as an ordinary Update.
        var fixture = BuildFixture(awaitsInitialPassword: true);
        await fixture.Repository.QueuePasswordChangesAsync([OwedPassword(fixture.Cso, PendingPasswordChangeStatus.Expired)]);

        await InvokeReconcileAsync(fixture);

        var pendingExport = fixture.Repository.PendingExports[fixture.PendingExportId];
        var change = pendingExport.AttributeValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending));
            Assert.That(change.AwaitsInitialPassword, Is.False);
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Pending));
            Assert.That(ExecutionItems(fixture.Processor), Is.Empty, "a release is silent");
        }
    }

    [Test]
    public async Task ReconcilePendingExports_NothingFlagged_NeverLooksUpOwedPasswordsAsync()
    {
        var fixture = BuildFixture(awaitsInitialPassword: false);

        await InvokeReconcileAsync(fixture);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Repository.GetMetaverseObjectIdsOwedPasswordCallCount, Is.Zero,
                "a deployment that never flags a change must not pay for the lookup");
            Assert.That(ExecutionItems(fixture.Processor).Count(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed),
                Is.EqualTo(1), "an unflagged unconfirmed change is reported exactly as before");
        }
    }

    [Test]
    public async Task ReconcilePendingExports_PasswordOwedWhenTheImportStartedAndDeliveredSince_StillHoldsItAsync()
    {
        // The password lane delivered (and enabled the account) after the import read it disabled, but before
        // reconciliation asked whether a password is owed. What the import read predates the delivery, so the change is
        // held for the next import rather than released against the interim value.
        var fixture = BuildFixture(awaitsInitialPassword: true);
        var owed = OwedPassword(fixture.Cso, PendingPasswordChangeStatus.Pending);
        await fixture.Repository.QueuePasswordChangesAsync([owed]);
        await InvokeRecordHoldsAtImportStartAsync(fixture);
        await DeliverEveryOwedPasswordAsync(fixture.Repository);

        await InvokeReconcileAsync(fixture);

        var change = fixture.Repository.PendingExports[fixture.PendingExportId].AttributeValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(change.AwaitsInitialPassword, Is.True);
            Assert.That(ExecutionItems(fixture.Processor), Is.Empty);
        }
    }

    [Test]
    public async Task JudgeUnreconciledInitialPasswordHolds_PasswordOwedWhenTheImportStartedAndDeliveredSince_LeavesItHeldAsync()
    {
        var fixture = BuildFixture(awaitsInitialPassword: true);
        var owed = OwedPassword(fixture.Cso, PendingPasswordChangeStatus.Pending);
        await fixture.Repository.QueuePasswordChangesAsync([owed]);
        await InvokeRecordHoldsAtImportStartAsync(fixture);
        await DeliverEveryOwedPasswordAsync(fixture.Repository);

        const string methodName = "JudgeUnreconciledInitialPasswordHoldsAsync";
        var method = typeof(SyncImportTaskProcessor).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{methodName} was not found. Update this fixture to invoke the current production method.");
        await (Task)method.Invoke(fixture.Processor, [new List<ConnectedSystemObject>()])!;

        var change = fixture.Repository.PendingExports[fixture.PendingExportId].AttributeValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
            Assert.That(change.AwaitsInitialPassword, Is.True, "the next import judges it against the enabled account");
        }
    }

    private static async Task DeliverEveryOwedPasswordAsync(SyncRepository repository)
    {
        // As the password delivery lane does: claim (Delivering), set the password, delete.
        var claimed = await repository.ClaimDuePasswordChangesAsync(ConnectedSystemId, "password-lane", DateTime.UtcNow.AddMinutes(1),
            TimeSpan.FromMinutes(5), 100, excludePropagated: false);
        await repository.DeletePasswordChangesAsync(claimed.Select(c => c.Id));
        Assert.That(repository.PendingPasswordChanges, Is.Empty, "the fixture delivered the password mid-import");
    }

    private static async Task InvokeRecordHoldsAtImportStartAsync(Fixture fixture)
    {
        const string methodName = "RecordHoldsStandingAtImportStartAsync";
        var method = typeof(SyncImportTaskProcessor).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{methodName} was not found. Update this fixture to invoke the current production method.");

        await (Task)method.Invoke(fixture.Processor, [])!;
    }

    private static PendingPasswordChange OwedPassword(ConnectedSystemObject cso, PendingPasswordChangeStatus status) => new()
    {
        Id = Guid.NewGuid(),
        MetaverseObjectId = cso.MetaverseObjectId!.Value,
        ConnectedSystemId = ConnectedSystemId,
        ConnectedSystemObjectId = cso.Id,
        Origin = PendingPasswordChangeOrigin.Provisioned,
        Status = status,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(1),
        ActivityId = Guid.NewGuid()
    };

    private static Task InvokeReconcileAsync(Fixture fixture) =>
        ImportReconciliationInvoker.ReconcileAsync(fixture.Processor, [fixture.Cso]);

    private static List<ActivityRunProfileExecutionItem> ExecutionItems(SyncImportTaskProcessor processor)
    {
        const string fieldName = "_activityRunProfileExecutionItems";
        var field = typeof(SyncImportTaskProcessor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"SyncImportTaskProcessor.{fieldName} was not found. Update this fixture if it has been renamed.");

        return (List<ActivityRunProfileExecutionItem>)field.GetValue(processor)!;
    }

    private sealed record Fixture(SyncImportTaskProcessor Processor, SyncRepository Repository, ConnectedSystemObject Cso, Guid PendingExportId);

    /// <summary>
    /// A joined account whose confirming import returned the disabled value, and its Pending Export asserting the
    /// enabled one, flagged or not as written with an interim value.
    /// </summary>
    private static Fixture BuildFixture(bool awaitsInitialPassword)
    {
        var repository = new SyncRepository();
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = UserAccountControlAttributeId, Name = "userAccountControl", Type = AttributeDataType.Number,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
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
                    Id = Guid.NewGuid(), AttributeId = UserAccountControlAttributeId, Attribute = attribute, IntValue = Disabled
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
                    AttributeId = UserAccountControlAttributeId,
                    Attribute = attribute,
                    ChangeType = PendingExportAttributeChangeType.Update,
                    Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
                    ExportAttemptCount = 1,
                    IntValue = Enabled,
                    AwaitsInitialPassword = awaitsInitialPassword
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
