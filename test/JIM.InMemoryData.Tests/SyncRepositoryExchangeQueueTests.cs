// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exchange;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using NUnit.Framework;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// The in-memory twin of the Exchange queue's list, summary and retry (#2021, Phase 6b) agrees with PostgreSQL
/// (ExchangeQueueDatabaseTests): it names its rows, reads them as their follow-ups do, narrows the retry exactly as the
/// list, and retries only what <see cref="PendingExchangeChange.Retry"/> would.
/// </summary>
[TestFixture]
public class SyncRepositoryExchangeQueueTests
{
    private static readonly DateTime AsOf = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

    private SyncRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
        _repo.SeedConnectedSystem(new ConnectedSystem { Id = 1, Name = "Panoply AD" });
        _repo.SeedConnectedSystem(new ConnectedSystem { Id = 2, Name = "Resurgam AD" });
    }

    [Test]
    public async Task GetPendingExchangeChangeHeadersAsync_EachRow_IsNamedAndReadsAsItsFollowUpAsync()
    {
        var parked = Seed(2, "Alan Turing", ActivityStatus.Complete, Parked);
        var notMade = Seed(1, "Barbara Liskov", ActivityStatus.CompleteWithWarning, c => c.Status = PendingExchangeChangeStatus.Refused);

        var page = await _repo.GetPendingExchangeChangeHeadersAsync(new PendingExchangeChangeFilter(), 0, 10, "identity", false, true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.TotalResults, Is.EqualTo(2));
            Assert.That(page.Results.Select(h => h.Id), Is.EqualTo(new[] { parked.Id, notMade.Id }), "Alan before Barbara");
            Assert.That(page.Results[0].ConnectedSystemName, Is.EqualTo("Resurgam AD"));
            Assert.That(page.Results[0].MetaverseObjectDisplayName, Is.EqualTo("Alan Turing"));
            Assert.That(page.Results[0].MetaverseObjectTypePluralName, Is.EqualTo("Users"));
            Assert.That(page.Results[0].State, Is.EqualTo(ExchangeChangeState.Parked));
            Assert.That(page.Results[1].State, Is.EqualTo(ExchangeChangeState.NotMade));
        }
    }

    [Test]
    public async Task RetryExchangeChangesAsync_SearchText_RetriesOnlyTheRetryableChangesTheListShowsAsync()
    {
        var shownParked = Seed(2, "Alan Turing", ActivityStatus.Complete, Parked);
        Seed(2, "Edsger Dijkstra", ActivityStatus.CompleteWithError, c => c.Status = PendingExchangeChangeStatus.Refused);
        Seed(2, "Grace Hopper", ActivityStatus.Complete, _ => { });
        var hiddenParked = Seed(1, "Ada Lovelace", ActivityStatus.Complete, Parked);
        var filter = new PendingExchangeChangeFilter { SearchText = "resurgam" };

        var retried = await _repo.RetryExchangeChangesAsync(filter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retried.Select(r => r.Id), Is.EqualTo(new[] { shownParked.Id }));
            Assert.That(retried[0].ActivityId, Is.EqualTo(shownParked.ActivityId));
            Assert.That(_repo.PendingExchangeChanges[shownParked.Id].IsDue(AsOf), Is.True);
            Assert.That(_repo.PendingExchangeChanges[hiddenParked.Id].Status, Is.EqualTo(PendingExchangeChangeStatus.Parked), "not shown, not retried");
        }
    }

    [Test]
    public async Task GetExchangeQueueSummaryAsync_CountsEveryStateAndWhatIsDueAsync()
    {
        Seed(1, "Ada Lovelace", ActivityStatus.Complete, _ => { });
        Seed(1, "Grace Hopper", ActivityStatus.Complete, c => { c.LastAttemptedAt = AsOf.AddMinutes(-2); c.NextRetryAt = AsOf.AddMinutes(5); });
        Seed(2, "Alan Turing", ActivityStatus.Complete, Parked);
        Seed(2, "Edsger Dijkstra", ActivityStatus.CompleteWithError, c => c.Status = PendingExchangeChangeStatus.Refused);

        var summary = await _repo.GetExchangeQueueSummaryAsync(AsOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.QueuedCount, Is.EqualTo(1));
            Assert.That(summary.RetryingCount, Is.EqualTo(1));
            Assert.That(summary.ParkedCount, Is.EqualTo(1));
            Assert.That(summary.RefusedCount, Is.EqualTo(1));
            Assert.That(summary.DueCount, Is.EqualTo(1), "only the queued change; the retry is still waiting out its backoff");
        }
    }

    private static void Parked(PendingExchangeChange change)
    {
        change.Status = PendingExchangeChangeStatus.Parked;
        change.AttemptCount = 1;
        change.LastAttemptedAt = AsOf.AddMinutes(-30);
        change.FirstFailedAt = AsOf.AddMinutes(-30);
        change.FailureKind = ExchangeChangeFailureKind.InsufficientPermissions;
    }

    private PendingExchangeChange Seed(int connectedSystemId, string identityName, ActivityStatus activityStatus, Action<PendingExchangeChange> leftAs)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = identityName, Type = new MetaverseObjectType { Name = "User", PluralName = "Users" } };
        _repo.SeedMetaverseObject(mvo);
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ExchangeRecipient,
            TargetOperationType = ActivityTargetOperationType.SetExchangeRecipient,
            Status = activityStatus,
            Created = AsOf.AddHours(-1)
        };
        _repo.SeedActivity(activity);
        var change = new PendingExchangeChange
        {
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectId = Guid.NewGuid(),
            MetaverseObjectId = mvo.Id,
            RecipientType = ExchangeRecipientTypes.Mailbox,
            CreatedAt = AsOf.AddHours(-1),
            ActivityId = activity.Id
        };
        leftAs(change);
        _repo.StageExchangeChangesAsync([change]).GetAwaiter().GetResult();
        // Staging writes the declared state and resets attempts; set the state the test needs on the stored row.
        var stored = _repo.PendingExchangeChanges[change.Id];
        stored.Status = change.Status;
        stored.AttemptCount = change.AttemptCount;
        stored.LastAttemptedAt = change.LastAttemptedAt;
        stored.FirstFailedAt = change.FirstFailedAt;
        stored.NextRetryAt = change.NextRetryAt;
        stored.FailureKind = change.FailureKind;
        return stored;
    }
}
