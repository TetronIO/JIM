// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exchange;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the Exchange queue as administrators see and act on it (#2021, Phase 6b): the list's
/// projection, filters and sorting, the summary, and the retry, which must act on exactly what the list shows (search
/// text included, #2725), reset exactly what <see cref="PendingExchangeChange.Retry"/> resets, and leave alone a change a
/// deliverer has claimed since the list was read.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ExchangeQueueDatabaseTests
{
    private static readonly DateTime AsOf = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    private static PostgresDataRepository Repository(JimDbContext context) => new(context);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Exchange queue tests.");
        _connectionString = connectionString;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    // ---- The list ---------------------------------------------------------------------------------------------------

    [Test]
    public async Task GetPendingExchangeChangeHeadersAsync_EveryRow_ReadsAsItsFollowUpWithItsNamesAsync()
    {
        var s = await SeedAsync();

        var page = await ListAsync(new PendingExchangeChangeFilter());

        var byId = page.Results.ToDictionary(h => h.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.TotalResults, Is.EqualTo(7));
            Assert.That(byId[s.Queued].State, Is.EqualTo(ExchangeChangeState.Queued));
            Assert.That(byId[s.Retrying].State, Is.EqualTo(ExchangeChangeState.Retrying));
            Assert.That(byId[s.Parked].State, Is.EqualTo(ExchangeChangeState.Parked));
            Assert.That(byId[s.Delivering].State, Is.EqualTo(ExchangeChangeState.Delivering));
            Assert.That(byId[s.Refused].State, Is.EqualTo(ExchangeChangeState.Refused));
            Assert.That(byId[s.NotMade].State, Is.EqualTo(ExchangeChangeState.NotMade), "the change Activity's warning tells it from a refusal");
            Assert.That(byId[s.RetryingWithNoIdentity].State, Is.EqualTo(ExchangeChangeState.Retrying));

            var parked = byId[s.Parked];
            Assert.That(parked.ConnectedSystemName, Is.EqualTo("Resurgam AD"));
            Assert.That(parked.MetaverseObjectDisplayName, Is.EqualTo("Alan Turing"));
            Assert.That(parked.MetaverseObjectTypePluralName, Is.EqualTo("Users"));
            Assert.That(parked.RecipientType, Is.EqualTo(ExchangeRecipientTypes.SharedMailbox));
            Assert.That(parked.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.InsufficientPermissions));
            Assert.That(parked.FailureMessage, Is.EqualTo("Access denied."));
            Assert.That(parked.AttemptCount, Is.EqualTo(1));
            Assert.That(parked.ActivityId, Is.Not.EqualTo(Guid.Empty));

            Assert.That(byId[s.Queued].ConnectedSystemObjectId, Is.EqualTo(s.AdaAccount));
            Assert.That(byId[s.RetryingWithNoIdentity].MetaverseObjectDisplayName, Is.Null, "a change outlives its identity and still lists");

            Assert.That(page.Results.Where(h => h.CanRetry).Select(h => h.Id),
                Is.EquivalentTo(new[] { s.Retrying, s.Parked, s.RetryingWithNoIdentity }));
        }
    }

    [Test]
    public async Task GetPendingExchangeChangeHeadersAsync_EachFilter_NarrowsToWhatItNamesAsync()
    {
        var s = await SeedAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await IdsAsync(new PendingExchangeChangeFilter { ConnectedSystemId = s.Resurgam }),
                Is.EquivalentTo(new[] { s.Parked, s.Refused, s.RetryingWithNoIdentity }));
            Assert.That(await IdsAsync(new PendingExchangeChangeFilter { State = ExchangeChangeState.Queued }), Is.EquivalentTo(new[] { s.Queued }));
            Assert.That(await IdsAsync(new PendingExchangeChangeFilter { State = ExchangeChangeState.Retrying }),
                Is.EquivalentTo(new[] { s.Retrying, s.RetryingWithNoIdentity }));
            Assert.That(await IdsAsync(new PendingExchangeChangeFilter { State = ExchangeChangeState.Delivering }), Is.EquivalentTo(new[] { s.Delivering }));
            Assert.That(await IdsAsync(new PendingExchangeChangeFilter { State = ExchangeChangeState.Parked }), Is.EquivalentTo(new[] { s.Parked }));
            Assert.That(await IdsAsync(new PendingExchangeChangeFilter { State = ExchangeChangeState.Refused }), Is.EquivalentTo(new[] { s.Refused }));
            Assert.That(await IdsAsync(new PendingExchangeChangeFilter { State = ExchangeChangeState.NotMade }), Is.EquivalentTo(new[] { s.NotMade }));
            Assert.That(await IdsAsync(new PendingExchangeChangeFilter { State = ExchangeChangeState.Delivered }), Is.Empty, "a delivered change has left the queue");
            Assert.That(await IdsAsync(new PendingExchangeChangeFilter { FailureKind = ExchangeChangeFailureKind.Unreachable }),
                Is.EquivalentTo(new[] { s.Retrying, s.RetryingWithNoIdentity }));
            Assert.That(await IdsAsync(new PendingExchangeChangeFilter { MetaverseObjectId = s.Grace }), Is.EquivalentTo(new[] { s.Retrying }));
            Assert.That(await IdsAsync(new PendingExchangeChangeFilter { ConnectedSystemObjectId = s.AdaAccount }), Is.EquivalentTo(new[] { s.Queued }));
            Assert.That(await IdsAsync(new PendingExchangeChangeFilter { Ids = [s.Parked, s.Queued], State = ExchangeChangeState.Parked }),
                Is.EquivalentTo(new[] { s.Parked }), "named rows still answer to the other criteria");
            Assert.That(await IdsAsync(new PendingExchangeChangeFilter { SearchText = "  turing " }), Is.EquivalentTo(new[] { s.Parked }), "the identity's name, trimmed, any case");
            Assert.That(await IdsAsync(new PendingExchangeChangeFilter { SearchText = "resurgam" }),
                Is.EquivalentTo(new[] { s.Parked, s.Refused, s.RetryingWithNoIdentity }), "the Connected System's name");
        }
    }

    [TestCase("identity", false)]
    [TestCase("system", true)]
    [TestCase("attempts", true)]
    [TestCase("nextattempt", false)]
    [TestCase("lastattempt", true)]
    [TestCase("queued", false)]
    public async Task GetPendingExchangeChangeHeadersAsync_Sorted_PagesInThatOrderWithoutRepeatsAsync(string sortBy, bool descending)
    {
        await SeedAsync();

        var all = (await ListAsync(new PendingExchangeChangeFilter(), sortBy: sortBy, descending: descending, count: 100)).Results;
        var first = await ListAsync(new PendingExchangeChangeFilter(), sortBy: sortBy, descending: descending, startIndex: 0, count: 4, includeTotalCount: false);
        var second = await ListAsync(new PendingExchangeChangeFilter(), sortBy: sortBy, descending: descending, startIndex: 4, count: 4, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.TotalResults, Is.Null, "not counted when not asked");
            Assert.That(first.Results.Concat(second.Results).Select(h => h.Id), Is.EqualTo(all.Select(h => h.Id)));
        }
    }

    [Test]
    public async Task GetExchangeQueueSummaryAsync_CountsEveryStateAndWhatIsDueAsync()
    {
        await SeedAsync();

        await using var read = NewContext();
        var summary = await Repository(read).Sync.GetExchangeQueueSummaryAsync(AsOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.QueuedCount, Is.EqualTo(1));
            Assert.That(summary.RetryingCount, Is.EqualTo(2));
            Assert.That(summary.DeliveringCount, Is.EqualTo(1));
            Assert.That(summary.ParkedCount, Is.EqualTo(1));
            Assert.That(summary.RefusedCount, Is.EqualTo(1));
            Assert.That(summary.NotMadeCount, Is.EqualTo(1));
            Assert.That(summary.DueCount, Is.EqualTo(2), "the queued change and the retry whose time has come; not the one still waiting out its backoff");
            Assert.That(summary.WaitingOnAPersonCount, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task SystemsNotManagingExchangeRecipients_TheirWaitingChangesReadAsHeldEverywhereAsync()
    {
        // The queued and retrying changes become Held, in the summary and the list alike; what waits on a person
        // (parked, refused, not made) and what is being delivered read as before.
        await SeedAsync();
        await using (var turnOff = NewContext())
            await turnOff.Database.ExecuteSqlRawAsync("UPDATE \"ConnectedSystems\" SET \"ManagesExchangeRecipients\" = false");

        await using var read = NewContext();
        var summary = await Repository(read).Sync.GetExchangeQueueSummaryAsync(AsOf);
        var held = await Repository(read).Sync.GetPendingExchangeChangeHeadersAsync(
            new PendingExchangeChangeFilter { State = ExchangeChangeState.Held }, 0, 50, "created", false, includeTotalCount: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.HeldCount, Is.EqualTo(3));
            Assert.That(summary.QueuedCount, Is.Zero);
            Assert.That(summary.RetryingCount, Is.Zero);
            Assert.That(summary.DueCount, Is.Zero);
            Assert.That(summary.DeliveringCount, Is.EqualTo(1));
            Assert.That(summary.WaitingOnAPersonCount, Is.EqualTo(3));
            Assert.That(held.Results.Select(h => h.State), Is.All.EqualTo(ExchangeChangeState.Held));
            Assert.That(held.TotalResults, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task GetExchangeQueueSummaryAsync_EmptyQueue_IsAllZeroAsync()
    {
        await using var read = NewContext();
        var summary = await Repository(read).Sync.GetExchangeQueueSummaryAsync(AsOf);

        Assert.That(summary.QueuedCount + summary.RetryingCount + summary.DeliveringCount + summary.WaitingOnAPersonCount + summary.DueCount, Is.Zero);
    }

    // ---- The retry --------------------------------------------------------------------------------------------------

    [Test]
    public async Task RetryExchangeChangesAsync_WholeQueue_ResetsOnlyTheRetryableAndSaysWhichAsync()
    {
        var s = await SeedAsync();

        await using (var act = NewContext())
        {
            var retried = await Repository(act).Sync.RetryExchangeChangesAsync(new PendingExchangeChangeFilter());

            Assert.That(retried.Select(r => r.Id), Is.EquivalentTo(new[] { s.Retrying, s.Parked, s.RetryingWithNoIdentity }));
            var parked = retried.Single(r => r.Id == s.Parked);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(parked.ActivityId, Is.EqualTo(s.ParkedActivity), "what the retry records its child Activity under");
                Assert.That(parked.ConnectedSystemId, Is.EqualTo(s.Resurgam));
                Assert.That(parked.ConnectedSystemName, Is.EqualTo("Resurgam AD"));
                Assert.That(parked.MetaverseObjectId, Is.EqualTo(s.Alan));
                Assert.That(parked.RecipientType, Is.EqualTo(ExchangeRecipientTypes.SharedMailbox));
            }
        }

        var rows = await RowsAsync();
        using (Assert.EnterMultipleScope())
        {
            foreach (var id in new[] { s.Retrying, s.Parked, s.RetryingWithNoIdentity })
            {
                var row = rows[id];
                Assert.That(row.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending), $"{id}");
                Assert.That(row.IsDue(AsOf), Is.True);
                Assert.That(row.AttemptCount, Is.Zero);
                Assert.That(row.LastAttemptedAt, Is.Null);
                Assert.That(row.FirstFailedAt, Is.Null);
                Assert.That(row.NextRetryAt, Is.Null);
                Assert.That(row.FailureKind, Is.Null);
                Assert.That(row.FailureMessage, Is.Null);
                Assert.That(row.ClaimedAt, Is.Null);
                Assert.That(row.ClaimedBy, Is.Null);
            }

            Assert.That(rows[s.Delivering].Status, Is.EqualTo(PendingExchangeChangeStatus.Delivering), "a delivery in flight is left to finish");
            Assert.That(rows[s.Delivering].ClaimedBy, Is.EqualTo("worker-1:aaaa1111"));
            Assert.That(rows[s.Refused].Status, Is.EqualTo(PendingExchangeChangeStatus.Refused));
            Assert.That(rows[s.Refused].FailureMessage, Is.EqualTo("A mailbox cannot be created on a contact."));
            Assert.That(rows[s.NotMade].Status, Is.EqualTo(PendingExchangeChangeStatus.Refused));
            Assert.That(rows[s.Queued].CreatedAt, Is.EqualTo(AsOf.AddHours(-3)), "a change never attempted is already due, and is not touched");
        }
    }

    [Test]
    public async Task RetryExchangeChangesAsync_SearchText_RetriesExactlyTheRetryableChangesTheListShowsAsync()
    {
        var s = await SeedAsync();
        var filter = new PendingExchangeChangeFilter { SearchText = "RESURGAM" };
        var shown = (await ListAsync(filter)).Results.Where(h => h.CanRetry).Select(h => h.Id).ToList();

        await using var act = NewContext();
        var retried = await Repository(act).Sync.RetryExchangeChangesAsync(filter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retried.Select(r => r.Id), Is.EquivalentTo(shown));
            Assert.That(retried.Select(r => r.Id), Is.EquivalentTo(new[] { s.Parked, s.RetryingWithNoIdentity }));
        }
    }

    [Test]
    public async Task RetryExchangeChangesAsync_ChangeClaimedSinceTheListWasRead_IsLeftToItsDelivererAsync()
    {
        var s = await SeedAsync();
        await using (var claim = NewContext())
        {
            var row = await claim.PendingExchangeChanges.SingleAsync(c => c.Id == s.Retrying);
            row.Claim("worker-2:bbbb2222", AsOf);
            await claim.SaveChangesAsync();
        }

        await using var act = NewContext();
        var retried = await Repository(act).Sync.RetryExchangeChangesAsync(new PendingExchangeChangeFilter { Ids = [s.Retrying] });

        var rows = await RowsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(retried, Is.Empty);
            Assert.That(rows[s.Retrying].Status, Is.EqualTo(PendingExchangeChangeStatus.Delivering));
            Assert.That(rows[s.Retrying].ClaimedBy, Is.EqualTo("worker-2:bbbb2222"));
        }
    }

    [Test]
    public async Task RetryExchangeChangesAsync_RetriedTwice_RetriesOnlyOnceAsync()
    {
        var s = await SeedAsync();
        var filter = new PendingExchangeChangeFilter { Ids = [s.Parked] };

        await using var act = NewContext();
        var first = await Repository(act).Sync.RetryExchangeChangesAsync(filter);
        var second = await Repository(act).Sync.RetryExchangeChangesAsync(filter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Has.Count.EqualTo(1));
            Assert.That(second, Is.Empty, "not attempted since, so nothing to retry");
        }
    }

    [Test]
    public async Task RetryExchangeChangesAsync_RaisesExchangeChangeNotificationAsync()
    {
        var s = await SeedAsync();
        var received = new List<(string Channel, string Payload)>();
        await using var listener = new NpgsqlConnection(_connectionString + ";Pooling=false");
        listener.Notification += (_, args) => received.Add((args.Channel, args.Payload));
        await listener.OpenAsync();
        await using (var listen = new NpgsqlCommand($"LISTEN \"{Constants.NotificationChannels.ExchangeChange}\"", listener))
            await listen.ExecuteNonQueryAsync();

        await using (var act = NewContext())
            await Repository(act).Sync.RetryExchangeChangesAsync(new PendingExchangeChangeFilter { Ids = [s.Parked] });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (received.Count == 0)
            await listener.WaitAsync(cts.Token);

        Assert.That(received[0], Is.EqualTo((Constants.NotificationChannels.ExchangeChange, s.Resurgam.ToString())),
            "the update wakes the Exchange delivery lane, so the retry is attempted within seconds");
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private async Task<RangeResultSet<PendingExchangeChangeHeader>> ListAsync(PendingExchangeChangeFilter filter, string sortBy = "queued",
        bool descending = false, int startIndex = 0, int count = 100, bool includeTotalCount = true)
    {
        await using var read = NewContext();
        return await Repository(read).Sync.GetPendingExchangeChangeHeadersAsync(filter, startIndex, count, sortBy, descending, includeTotalCount);
    }

    private async Task<List<Guid>> IdsAsync(PendingExchangeChangeFilter filter) =>
        (await ListAsync(filter)).Results.Select(h => h.Id).ToList();

    private async Task<Dictionary<Guid, PendingExchangeChange>> RowsAsync()
    {
        await using var read = NewContext();
        return await read.PendingExchangeChanges.AsNoTracking().ToDictionaryAsync(c => c.Id);
    }

    private sealed record Seeded(
        int Panoply, int Resurgam, Guid Grace, Guid Alan, Guid AdaAccount,
        Guid Queued, Guid Retrying, Guid Parked, Guid ParkedActivity, Guid Delivering, Guid Refused, Guid NotMade, Guid RetryingWithNoIdentity);

    /// <summary>
    /// Seven changes, one per on-queue state, over two Connected Systems, each with its own Activity and, bar one, an
    /// identity. Only Ada's names an account: the queue allows any number of changes with none, which spares seeding one
    /// per change.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Panoply AD Connector", BuiltIn = true };
        var panoply = new ConnectedSystem { Name = "Panoply AD", ConnectorDefinition = connectorDefinition, ManagesExchangeRecipients = true };
        var resurgam = new ConnectedSystem { Name = "Resurgam AD", ConnectorDefinition = connectorDefinition, ManagesExchangeRecipients = true };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = panoply, Selected = true };
        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = false };
        seed.AddRange(connectorDefinition, panoply, resurgam, csType, mvType);
        await seed.SaveChangesAsync();

        MetaverseObject Identity(string name)
        {
            var mvo = new MetaverseObject { Type = mvType, CachedDisplayName = name };
            seed.Add(mvo);
            return mvo;
        }

        var ada = Identity("Ada Lovelace");
        var grace = Identity("Grace Hopper");
        var alan = Identity("Alan Turing");
        var katherine = Identity("Katherine Johnson");
        var edsger = Identity("Edsger Dijkstra");
        var barbara = Identity("Barbara Liskov");
        var adaAccount = new ConnectedSystemObject { TypeId = csType.Id, ConnectedSystemId = panoply.Id, Status = ConnectedSystemObjectStatus.Normal };
        seed.Add(adaAccount);
        await seed.SaveChangesAsync();

        PendingExchangeChange Change(ConnectedSystem system, MetaverseObject? identity, ActivityStatus activityStatus, Action<PendingExchangeChange> leftAs,
            string recipientType = ExchangeRecipientTypes.Mailbox, Guid? account = null)
        {
            var activity = new Activity
            {
                Id = Guid.NewGuid(),
                TargetType = ActivityTargetType.ExchangeRecipient,
                TargetOperationType = ActivityTargetOperationType.SetExchangeRecipient,
                TargetContext = recipientType,
                TargetName = system.Name,
                ConnectedSystemId = system.Id,
                MetaverseObjectId = identity?.Id,
                Status = activityStatus,
                InitiatedByType = ActivityInitiatorType.System,
                InitiatedByName = "System",
                Created = AsOf.AddHours(-3),
                Executed = AsOf.AddHours(-3)
            };
            seed.Activities.Add(activity);
            var change = new PendingExchangeChange
            {
                ConnectedSystemId = system.Id,
                ConnectedSystemObjectId = account,
                MetaverseObjectId = identity?.Id,
                RecipientType = recipientType,
                CreatedAt = AsOf.AddHours(-3),
                ActivityId = activity.Id
            };
            leftAs(change);
            seed.PendingExchangeChanges.Add(change);
            return change;
        }

        var queued = Change(panoply, ada, ActivityStatus.Complete, _ => { }, account: adaAccount.Id);
        var retrying = Change(panoply, grace, ActivityStatus.Complete, c =>
        {
            c.CreatedAt = AsOf.AddHours(-2);
            c.AttemptCount = 2;
            c.LastAttemptedAt = AsOf.AddMinutes(-2);
            c.FirstFailedAt = AsOf.AddMinutes(-10);
            c.NextRetryAt = AsOf.AddMinutes(5);
            c.FailureKind = ExchangeChangeFailureKind.Unreachable;
            c.FailureMessage = "Exchange did not answer within 60 seconds.";
        });
        var parked = Change(resurgam, alan, ActivityStatus.Complete, c =>
        {
            c.CreatedAt = AsOf.AddHours(-1);
            c.Status = PendingExchangeChangeStatus.Parked;
            c.AttemptCount = 1;
            c.LastAttemptedAt = AsOf.AddMinutes(-30);
            c.FirstFailedAt = AsOf.AddMinutes(-30);
            c.FailureKind = ExchangeChangeFailureKind.InsufficientPermissions;
            c.FailureMessage = "Access denied.";
        }, recipientType: ExchangeRecipientTypes.SharedMailbox);
        var delivering = Change(panoply, katherine, ActivityStatus.Complete, c =>
        {
            c.CreatedAt = AsOf.AddMinutes(-50);
            c.Status = PendingExchangeChangeStatus.Delivering;
            c.ClaimedAt = AsOf.AddSeconds(-20);
            c.ClaimedBy = "worker-1:aaaa1111";
        });
        var refused = Change(resurgam, edsger, ActivityStatus.CompleteWithError, c =>
        {
            c.CreatedAt = AsOf.AddMinutes(-40);
            c.Status = PendingExchangeChangeStatus.Refused;
            c.FailureMessage = "A mailbox cannot be created on a contact.";
        });
        var notMade = Change(panoply, barbara, ActivityStatus.CompleteWithWarning, c =>
        {
            c.CreatedAt = AsOf.AddMinutes(-30);
            c.Status = PendingExchangeChangeStatus.Refused;
            c.FailureMessage = "Changing a recipient's type is not supported in this release.";
        });
        var noIdentity = Change(resurgam, null, ActivityStatus.Complete, c =>
        {
            c.CreatedAt = AsOf.AddMinutes(-20);
            c.AttemptCount = 1;
            c.LastAttemptedAt = AsOf.AddMinutes(-3);
            c.FirstFailedAt = AsOf.AddMinutes(-3);
            c.NextRetryAt = AsOf.AddMinutes(-1);
            c.FailureKind = ExchangeChangeFailureKind.Unreachable;
        });
        await seed.SaveChangesAsync();

        return new Seeded(panoply.Id, resurgam.Id, grace.Id, alan.Id, adaAccount.Id,
            queued.Id, retrying.Id, parked.Id, parked.ActivityId, delivering.Id, refused.Id, notMade.Id, noIdentity.Id);
    }
}
