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
/// The confirming import's side of #2039 Phase 2, through <c>SyncImportTaskProcessor.ReconcilePendingExportsAsync</c>:
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

    private static async Task InvokeReconcileAsync(Fixture fixture)
    {
        const string methodName = "ReconcilePendingExportsAsync";
        var method = typeof(SyncImportTaskProcessor).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{methodName} was not found. Update this fixture to invoke the current production method.");

        await (Task)method.Invoke(fixture.Processor,
            [new List<ConnectedSystemObject> { fixture.Cso }, new Dictionary<Guid, ActivityRunProfileExecutionItem>(), ConnectedSystemId, null])!;
    }

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
