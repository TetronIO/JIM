// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// The in-memory twin of the Exchange queue's staging statement (#2021) must decide exactly as the PostgreSQL one does,
/// because the unit and workflow tests of export staging run over it: one row per account, a newer declared state
/// declaring a different state supersedes the row in place (whatever its status, a delivery in flight included), and
/// re-staging the same declared state leaves the row as it is, whatever its status.
/// </summary>
[TestFixture]
public class SyncRepositoryExchangeChangeStagingTests
{
    private SyncRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    private static PendingExchangeChange Change(Guid csoId, string recipientType = "Mailbox", string? alias = "ada.lovelace") => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = 3,
        ConnectedSystemObjectId = csoId,
        MetaverseObjectId = Guid.NewGuid(),
        SyncRuleId = 5,
        RecipientType = recipientType,
        Alias = alias,
        Database = "DB01",
        PrimarySmtpAddress = null,
        ExternalAddress = null,
        DomainController = "dc01.corp.example",
        CreatedAt = DateTime.UtcNow,
        ActivityId = Guid.NewGuid()
    };

    /// <summary>A row already on the queue, in the state an earlier attempt or an administrator left it.</summary>
    private async Task<PendingExchangeChange> StagedAsync(Guid csoId, Action<PendingExchangeChange> leftAs, string? alias = "ada.lovelace")
    {
        var change = Change(csoId, alias: alias);
        await _repo.StageExchangeChangesAsync([change]);
        leftAs(_repo.PendingExchangeChanges[change.Id]);
        return _repo.PendingExchangeChanges[change.Id];
    }

    [Test]
    public async Task StageExchangeChangesAsync_NoRowForTheAccount_InsertsAsync()
    {
        var change = Change(Guid.NewGuid());

        var outcomes = await _repo.StageExchangeChangesAsync([change]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes, Has.Count.EqualTo(1));
            Assert.That(outcomes[0].Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Inserted));
            Assert.That(outcomes[0].RequestedId, Is.EqualTo(change.Id));
            Assert.That(outcomes[0].RowId, Is.EqualTo(change.Id));
            Assert.That(outcomes[0].PreviousActivityId, Is.Null);
            Assert.That(_repo.PendingExchangeChanges, Has.Count.EqualTo(1));
            Assert.That(_repo.PendingExchangeChanges[change.Id].RecipientType, Is.EqualTo("Mailbox"));
        }
    }

    [Test]
    public async Task StageExchangeChangesAsync_ParkedRowForTheAccount_SupersedesInPlaceAndResetsItAsync()
    {
        var csoId = Guid.NewGuid();
        var existing = await StagedAsync(csoId, row =>
        {
            row.Status = PendingExchangeChangeStatus.Parked;
            row.FailureKind = ExchangeChangeFailureKind.Refused;
            row.FailureMessage = "The alias is already in use.";
            row.AttemptCount = 4;
            row.LastAttemptedAt = DateTime.UtcNow.AddMinutes(-5);
            row.FirstFailedAt = DateTime.UtcNow.AddHours(-25);
            row.NextRetryAt = DateTime.UtcNow.AddMinutes(10);
        });
        var existingId = existing.Id;
        var previousActivityId = existing.ActivityId;
        var newer = Change(csoId, recipientType: "SharedMailbox", alias: "reception");
        newer.DomainController = "dc02.corp.example";

        var outcomes = await _repo.StageExchangeChangesAsync([newer]);

        var stored = _repo.PendingExchangeChanges.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Superseded));
            Assert.That(outcomes[0].RowId, Is.EqualTo(existingId));
            Assert.That(outcomes[0].RequestedId, Is.Not.EqualTo(existingId));
            Assert.That(outcomes[0].PreviousActivityId, Is.EqualTo(previousActivityId));
            Assert.That(newer.Id, Is.EqualTo(existingId), "the caller holds the id actually in the queue");
            Assert.That(stored.Id, Is.EqualTo(existingId));
            Assert.That(stored.RecipientType, Is.EqualTo("SharedMailbox"));
            Assert.That(stored.Alias, Is.EqualTo("reception"));
            Assert.That(stored.DomainController, Is.EqualTo("dc02.corp.example"));
            Assert.That(stored.MetaverseObjectId, Is.EqualTo(newer.MetaverseObjectId));
            Assert.That(stored.ActivityId, Is.EqualTo(newer.ActivityId));
            Assert.That(stored.CreatedAt, Is.EqualTo(newer.CreatedAt));
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.FailureKind, Is.Null);
            Assert.That(stored.FailureMessage, Is.Null);
            Assert.That(stored.AttemptCount, Is.Zero);
            Assert.That(stored.LastAttemptedAt, Is.Null);
            Assert.That(stored.FirstFailedAt, Is.Null);
            Assert.That(stored.NextRetryAt, Is.Null);
        }
    }

    [Test]
    public async Task StageExchangeChangesAsync_DeliveringRowForTheAccount_SupersedesItAndClearsTheClaimAsync()
    {
        var csoId = Guid.NewGuid();
        var existing = await StagedAsync(csoId, row =>
        {
            row.Status = PendingExchangeChangeStatus.Delivering;
            row.ClaimedAt = DateTime.UtcNow;
            row.ClaimedBy = "worker-1a2b3c4d";
            row.AttemptCount = 1;
        });
        var existingId = existing.Id;

        var outcomes = await _repo.StageExchangeChangesAsync([Change(csoId, alias: "ada")]);

        var stored = _repo.PendingExchangeChanges[existingId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Superseded));
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.ClaimedAt, Is.Null);
            Assert.That(stored.ClaimedBy, Is.Null);
            Assert.That(stored.AttemptCount, Is.Zero);
            Assert.That(stored.Alias, Is.EqualTo("ada"));
        }
    }

    [Test]
    public async Task StageExchangeChangesAsync_CancelledRowWithTheSameDeclaredState_LeavesItUnchangedAsync()
    {
        var csoId = Guid.NewGuid();
        var cancelledAt = DateTime.UtcNow.AddMinutes(-30);
        var cancelledById = Guid.NewGuid();
        // Declared with a null alias; re-staged with an empty one, which declares the same thing.
        var existing = await StagedAsync(csoId, row =>
        {
            row.Status = PendingExchangeChangeStatus.Cancelled;
            row.CancelledAt = cancelledAt;
            row.CancelledById = cancelledById;
            row.CancelledByName = "Ada Lovelace";
        }, alias: null);
        var existingId = existing.Id;
        var activityId = existing.ActivityId;
        var restaged = Change(csoId, alias: "");

        var outcomes = await _repo.StageExchangeChangesAsync([restaged]);

        var stored = _repo.PendingExchangeChanges[existingId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Unchanged));
            Assert.That(outcomes[0].ExistingStatus, Is.EqualTo(PendingExchangeChangeStatus.Cancelled));
            Assert.That(outcomes[0].RowId, Is.EqualTo(existingId));
            Assert.That(outcomes[0].PreviousActivityId, Is.Null);
            Assert.That(restaged.Id, Is.EqualTo(existingId));
            Assert.That(_repo.PendingExchangeChanges, Has.Count.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Cancelled));
            Assert.That(stored.CancelledAt, Is.EqualTo(cancelledAt));
            Assert.That(stored.CancelledById, Is.EqualTo(cancelledById));
            Assert.That(stored.CancelledByName, Is.EqualTo("Ada Lovelace"));
            Assert.That(stored.ActivityId, Is.EqualTo(activityId), "a held row keeps its own Activity");
            Assert.That(stored.DomainController, Is.EqualTo("dc01.corp.example"));
        }
    }

    /// <summary>
    /// Re-staging the declared state a Parked row already carries leaves it parked, with its failure and attempts: a
    /// parked change holds until a person retries it or the declared state changes (PRD requirement 11), and an export
    /// re-evaluated as unconfirmed must not unpark it.
    /// </summary>
    [Test]
    public async Task StageExchangeChangesAsync_ParkedRowWithTheSameDeclaredState_LeavesItUnchangedAsync()
    {
        var csoId = Guid.NewGuid();
        var firstFailedAt = DateTime.UtcNow.AddHours(-25);
        var existing = await StagedAsync(csoId, row =>
        {
            row.Status = PendingExchangeChangeStatus.Parked;
            row.FailureKind = ExchangeChangeFailureKind.Refused;
            row.FailureMessage = "The alias is already in use.";
            row.AttemptCount = 4;
            row.FirstFailedAt = firstFailedAt;
        });
        var existingId = existing.Id;
        var activityId = existing.ActivityId;
        var restaged = Change(csoId);
        restaged.DomainController = "dc02.corp.example";

        var outcomes = await _repo.StageExchangeChangesAsync([restaged]);

        var stored = _repo.PendingExchangeChanges[existingId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Unchanged));
            Assert.That(outcomes[0].ExistingStatus, Is.EqualTo(PendingExchangeChangeStatus.Parked));
            Assert.That(outcomes[0].RowId, Is.EqualTo(existingId));
            Assert.That(outcomes[0].PreviousActivityId, Is.Null);
            Assert.That(restaged.Id, Is.EqualTo(existingId), "the caller holds the id actually in the queue");
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Parked));
            Assert.That(stored.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Refused));
            Assert.That(stored.FailureMessage, Is.EqualTo("The alias is already in use."));
            Assert.That(stored.AttemptCount, Is.EqualTo(4));
            Assert.That(stored.FirstFailedAt, Is.EqualTo(firstFailedAt));
            Assert.That(stored.ActivityId, Is.EqualTo(activityId));
            Assert.That(stored.DomainController, Is.EqualTo("dc01.corp.example"), "an unchanged row is left untouched");
        }
    }

    /// <summary>
    /// Re-staging the declared state a retrying row already carries keeps its backoff: wiping the attempts and the
    /// next retry time would have every unconfirmed export hammer an Exchange that is already refusing.
    /// </summary>
    [Test]
    public async Task StageExchangeChangesAsync_PendingRowWithAttemptsAndTheSameDeclaredState_KeepsItsBackoffAsync()
    {
        var csoId = Guid.NewGuid();
        var nextRetryAt = DateTime.UtcNow.AddMinutes(8);
        var existing = await StagedAsync(csoId, row =>
        {
            row.FailureKind = ExchangeChangeFailureKind.Unreachable;
            row.AttemptCount = 2;
            row.FirstFailedAt = DateTime.UtcNow.AddMinutes(-6);
            row.NextRetryAt = nextRetryAt;
        });
        var existingId = existing.Id;

        var outcomes = await _repo.StageExchangeChangesAsync([Change(csoId)]);

        var stored = _repo.PendingExchangeChanges[existingId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Unchanged));
            Assert.That(outcomes[0].ExistingStatus, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.AttemptCount, Is.EqualTo(2));
            Assert.That(stored.NextRetryAt, Is.EqualTo(nextRetryAt));
            Assert.That(stored.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Unreachable));
        }
    }

    [Test]
    public async Task StageExchangeChangesAsync_CancelledRowWithADifferentDeclaredState_SupersedesAndClearsTheCancellationAsync()
    {
        var csoId = Guid.NewGuid();
        var existing = await StagedAsync(csoId, row =>
        {
            row.Status = PendingExchangeChangeStatus.Cancelled;
            row.CancelledAt = DateTime.UtcNow;
            row.CancelledById = Guid.NewGuid();
            row.CancelledByName = "Ada Lovelace";
        });
        var existingId = existing.Id;

        var outcomes = await _repo.StageExchangeChangesAsync([Change(csoId, recipientType: "MailUser")]);

        var stored = _repo.PendingExchangeChanges[existingId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Superseded));
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.RecipientType, Is.EqualTo("MailUser"));
            Assert.That(stored.CancelledAt, Is.Null);
            Assert.That(stored.CancelledById, Is.Null);
            Assert.That(stored.CancelledByName, Is.Null);
        }
    }

    [Test]
    public async Task StageExchangeChangesAsync_DifferentAccounts_EachGetsItsOwnRowAsync()
    {
        var outcomes = await _repo.StageExchangeChangesAsync([Change(Guid.NewGuid()), Change(Guid.NewGuid())]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes.Select(o => o.Disposition), Is.All.EqualTo(ExchangeChangeStagingDisposition.Inserted));
            Assert.That(_repo.PendingExchangeChanges, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void StageExchangeChangesAsync_ChangeNamingNoAccount_Throws()
    {
        var change = Change(Guid.NewGuid());
        change.ConnectedSystemObjectId = null;

        Assert.That(() => _repo.StageExchangeChangesAsync([change]), Throws.ArgumentException);
    }
}
