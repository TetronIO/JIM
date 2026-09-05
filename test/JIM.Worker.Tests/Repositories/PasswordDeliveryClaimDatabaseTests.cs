// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the Password Delivery Service's claim (#1635): the one statement that selects
/// due rows with <c>FOR UPDATE SKIP LOCKED</c>, marks them Delivering and returns them, and everything that has to
/// agree with it.
/// <para>
/// None of this is reachable from the unit suite. The claim's safety against a second deliverer is a property of
/// row locks inside one statement, which the in-memory fake cannot have; the guard on the attempt write is a
/// WHERE clause on the row's stored status; and the reads that decide what is due join the queue to each system's
/// configuration, which the fake does not hold.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c>
/// fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is absent. Do NOT run this fixture outside the sanctioned
/// scratch-database workflow: <c>SetUp</c> TRUNCATEs every table.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PasswordDeliveryClaimDatabaseTests
{
    private const string Claimant = "worker-a-1a2b3c4d";
    private const string OtherClaimant = "worker-b-5e6f7a8b";
    private static readonly TimeSpan Lease = PendingPasswordChange.ClaimLease;
    private static readonly DateTime AsOf = new(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Password Delivery claim tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory') LOOP
                    EXECUTE 'TRUNCATE TABLE ""' || r.tablename || '"" RESTART IDENTITY CASCADE';
                END LOOP;
            END $$;");
    }

    #region seeding

    /// <summary>
    /// A Connected System configured for Password Synchronisation, with one Metaverse Object Type to hang
    /// identities off. Returns the system id.
    /// </summary>
    private async Task<int> SeedSystemAsync(string name = "Corporate AD", bool enabled = true)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = $"{name} Connector", SupportsPasswordSet = true };
        var system = new ConnectedSystem { Name = name, ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        seed.AddRange(connectorDefinition, system, objectType);
        if (!await seed.MetaverseObjectTypes.AnyAsync())
            seed.Add(new MetaverseObjectType { Name = "User", PluralName = "Users" });
        await seed.SaveChangesAsync();

        seed.Add(new ConnectedSystemPasswordSynchronisation
        {
            ConnectedSystemId = system.Id,
            Enabled = enabled,
            TargetObjectTypeId = objectType.Id
        });
        await seed.SaveChangesAsync();

        return system.Id;
    }

    /// <summary>
    /// One queued change for a fresh identity on the given system, adjusted as the test wants, inserted directly
    /// so a test can seed any state the queue can hold. Returns the row's id.
    /// </summary>
    private async Task<Guid> SeedChangeAsync(int systemId, Action<PendingPasswordChange>? adjust = null, DateTime? createdAt = null)
    {
        await using var seed = NewContext();

        var mvType = await seed.MetaverseObjectTypes.FirstAsync();
        var mvo = new MetaverseObject { Type = mvType };
        seed.Add(mvo);
        await seed.SaveChangesAsync();

        var created = createdAt ?? AsOf.AddMinutes(-5);
        var change = new PendingPasswordChange
        {
            MetaverseObjectId = mvo.Id,
            ConnectedSystemId = systemId,
            EncryptedPassword = "$JIMPW$v1$ciphertext",
            CreatedAt = created,
            ExpiresAt = created.AddDays(7),
            ActivityId = Guid.NewGuid()
        };
        adjust?.Invoke(change);

        seed.Add(change);
        await seed.SaveChangesAsync();
        return change.Id;
    }

    private async Task<PendingPasswordChange> StoredAsync(Guid id)
    {
        await using var ctx = NewContext();
        return await ctx.PendingPasswordChanges.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private async Task<List<PendingPasswordChange>> ClaimAsync(int systemId, string claimant = Claimant, DateTime? asOf = null, int maximum = 100)
    {
        await using var ctx = NewContext();
        return await new PostgresDataRepository(ctx).Sync.ClaimDuePasswordChangesAsync(systemId, claimant, asOf ?? AsOf, Lease, maximum);
    }

    #endregion

    [Test]
    public async Task ClaimDuePasswordChangesAsync_ReturnsDueRowsAndMarksThemDeliveringAsync()
    {
        var systemId = await SeedSystemAsync();
        var first = await SeedChangeAsync(systemId, createdAt: AsOf.AddMinutes(-10));
        var second = await SeedChangeAsync(systemId, createdAt: AsOf.AddMinutes(-5));

        var claimed = await ClaimAsync(systemId);

        var stored = await StoredAsync(first);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimed.Select(c => c.Id), Is.EqualTo(new[] { first, second }), "Oldest first, so nothing starves.");
            Assert.That(claimed.Select(c => c.Status), Is.All.EqualTo(PendingPasswordChangeStatus.Delivering),
                "The rows come back as claimed, so the caller holds exactly what the database holds.");
            Assert.That(claimed.Select(c => c.ClaimedBy), Is.All.EqualTo(Claimant));
            Assert.That(claimed.Select(c => c.ClaimedAt), Is.All.EqualTo(AsOf));
            Assert.That(claimed[0].EncryptedPassword, Is.EqualTo("$JIMPW$v1$ciphertext"), "The lane decrypts from what it claimed.");
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Delivering));
            Assert.That(stored.ClaimedBy, Is.EqualTo(Claimant));
            Assert.That(stored.ClaimedAt, Is.EqualTo(AsOf));
        }
    }

    [Test]
    public async Task ClaimDuePasswordChangesAsync_HonoursTheMaximumAsync()
    {
        var systemId = await SeedSystemAsync();
        await SeedChangeAsync(systemId, createdAt: AsOf.AddMinutes(-3));
        await SeedChangeAsync(systemId, createdAt: AsOf.AddMinutes(-2));
        await SeedChangeAsync(systemId, createdAt: AsOf.AddMinutes(-1));

        var claimed = await ClaimAsync(systemId, maximum: 2);

        await using var ctx = NewContext();
        var delivering = await ctx.PendingPasswordChanges.AsNoTracking().CountAsync(c => c.Status == PendingPasswordChangeStatus.Delivering);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimed, Has.Count.EqualTo(2));
            Assert.That(delivering, Is.EqualTo(2), "The third row is left Pending for the next claim.");
        }
    }

    [Test]
    public async Task ClaimDuePasswordChangesAsync_SkipsWhatIsNotDueAsync()
    {
        var systemId = await SeedSystemAsync();
        var other = await SeedSystemAsync("HR Portal");
        var due = await SeedChangeAsync(systemId);
        await SeedChangeAsync(systemId, c => c.NextRetryAt = AsOf.AddMinutes(1));
        await SeedChangeAsync(systemId, c => c.Status = PendingPasswordChangeStatus.Parked);
        await SeedChangeAsync(systemId, c => c.Status = PendingPasswordChangeStatus.Expired);
        await SeedChangeAsync(systemId, c => c.Status = PendingPasswordChangeStatus.Cancelled);
        await SeedChangeAsync(other);

        var claimed = await ClaimAsync(systemId);

        Assert.That(claimed.Select(c => c.Id), Is.EqualTo(new[] { due }),
            "Only a pending, due change on the named system is anybody's to take.");
    }

    [Test]
    public async Task ClaimDuePasswordChangesAsync_RetryThatHasComeDue_IsClaimedAsync()
    {
        var systemId = await SeedSystemAsync();
        var retry = await SeedChangeAsync(systemId, c =>
        {
            c.AttemptCount = 1;
            c.NextRetryAt = AsOf.AddMinutes(-1);
        });

        var claimed = await ClaimAsync(systemId);

        Assert.That(claimed.Select(c => c.Id), Is.EqualTo(new[] { retry }));
    }

    /// <summary>
    /// The property the whole claim exists for. The first claimer holds its statement's transaction open, so its
    /// rows are still locked when the second claims: SKIP LOCKED has the second step over them rather than wait,
    /// and the two end up with disjoint halves of the queue. A read followed by a write would have handed both
    /// the same rows.
    /// </summary>
    [Test]
    public async Task ClaimDuePasswordChangesAsync_TwoConcurrentClaimers_SplitTheRowsWithNoOverlapAsync()
    {
        var systemId = await SeedSystemAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
            ids.Add(await SeedChangeAsync(systemId, createdAt: AsOf.AddMinutes(-10 + i)));

        await using var first = NewContext();
        await using var transaction = await first.Database.BeginTransactionAsync();
        var firstClaim = await new PostgresDataRepository(first).Sync.ClaimDuePasswordChangesAsync(systemId, Claimant, AsOf, Lease, 3);

        // Still inside the first claimer's transaction: its three rows are locked and marked, not yet committed.
        var secondClaim = await ClaimAsync(systemId, OtherClaimant, maximum: 10);

        await transaction.CommitAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstClaim, Has.Count.EqualTo(3));
            Assert.That(secondClaim, Has.Count.EqualTo(3), "The second claimer takes what the first did not, without waiting for it.");
            Assert.That(firstClaim.Select(c => c.Id).Intersect(secondClaim.Select(c => c.Id)), Is.Empty, "No row is claimed twice.");
            Assert.That(firstClaim.Select(c => c.Id).Concat(secondClaim.Select(c => c.Id)), Is.EquivalentTo(ids), "Nothing is left behind.");
        }

        await using var verify = NewContext();
        var byClaimant = await verify.PendingPasswordChanges.AsNoTracking()
            .GroupBy(c => c.ClaimedBy)
            .Select(g => new { Claimant = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Claimant!, g => g.Count);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(byClaimant[Claimant], Is.EqualTo(3));
            Assert.That(byClaimant[OtherClaimant], Is.EqualTo(3));
        }
    }

    [Test]
    public async Task ClaimDuePasswordChangesAsync_FreshClaim_IsNotReclaimableAsync()
    {
        var systemId = await SeedSystemAsync();
        await SeedChangeAsync(systemId);
        await ClaimAsync(systemId);

        var reclaimed = await ClaimAsync(systemId, OtherClaimant, AsOf + Lease - TimeSpan.FromSeconds(1));

        Assert.That(reclaimed, Is.Empty, "A claim within its lease belongs to whoever holds it.");
    }

    [Test]
    public async Task ClaimDuePasswordChangesAsync_ExpiredLease_IsReclaimableAsync()
    {
        // A deliverer that died mid-flight leaves its rows Delivering; the lease running out is what gives them
        // back, to whichever deliverer asks next.
        var systemId = await SeedSystemAsync();
        var id = await SeedChangeAsync(systemId);
        await ClaimAsync(systemId);

        var reclaimedAt = AsOf + Lease;
        var reclaimed = await ClaimAsync(systemId, OtherClaimant, reclaimedAt);

        var stored = await StoredAsync(id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reclaimed.Select(c => c.Id), Is.EqualTo(new[] { id }));
            Assert.That(stored.ClaimedBy, Is.EqualTo(OtherClaimant));
            Assert.That(stored.ClaimedAt, Is.EqualTo(reclaimedAt), "The lease starts again from the new claim.");
        }
    }

    [Test]
    public async Task GetConnectedSystemIdsWithDuePasswordChangesAsync_IncludesASystemHoldingAnExpiredClaimAsync()
    {
        var claimedSystem = await SeedSystemAsync("Corporate AD");
        var freshSystem = await SeedSystemAsync("HR Portal");
        await SeedChangeAsync(claimedSystem);
        await SeedChangeAsync(freshSystem);
        await ClaimAsync(claimedSystem);
        await ClaimAsync(freshSystem, asOf: AsOf + Lease);

        await using var ctx = NewContext();
        var systems = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemIdsWithDuePasswordChangesAsync(AsOf + Lease, Lease);

        Assert.That(systems, Is.EqualTo(new[] { claimedSystem }),
            "The first system's claim has run out and is claimable; the second's is fresh and is not.");
    }

    [Test]
    public async Task ExpirePasswordChangesAsync_IgnoresDeliveringRowsAsync()
    {
        // The deliverer holding the row records its own outcome; two writers deciding the same row's fate at
        // once is what the claim exists to prevent.
        var systemId = await SeedSystemAsync();
        var firstId = await SeedChangeAsync(systemId, c => c.ExpiresAt = AsOf.AddMinutes(-1));
        var secondId = await SeedChangeAsync(systemId, c => c.ExpiresAt = AsOf.AddMinutes(-1));

        // Both rows share a CreatedAt, so the claim's tiebreak (the random Id) decides which one it takes; read
        // that back rather than assuming, or the test passes or fails on the luck of two Guids.
        var claimedId = (await ClaimAsync(systemId, maximum: 1)).Single().Id;
        var pendingId = claimedId == firstId ? secondId : firstId;

        await using var ctx = NewContext();
        var expired = await new PostgresDataRepository(ctx).Sync.ExpirePasswordChangesAsync(systemId, AsOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expired, Is.EqualTo(1));
            Assert.That((await StoredAsync(claimedId)).Status, Is.EqualTo(PendingPasswordChangeStatus.Delivering));
            Assert.That((await StoredAsync(pendingId)).Status, Is.EqualTo(PendingPasswordChangeStatus.Expired));
        }
    }

    [Test]
    public async Task RecordPasswordChangeAttemptsAsync_EndsTheClaimAsync()
    {
        var systemId = await SeedSystemAsync();
        var id = await SeedChangeAsync(systemId);
        var claimed = (await ClaimAsync(systemId)).Single();

        claimed.RecordAttempt(PasswordSetFailureReason.Transient, "Server unavailable",
            new ConnectedSystemPasswordSynchronisation { MaxRetries = 3, RetryBackoffBase = TimeSpan.FromMinutes(5) }, AsOf.AddSeconds(2));
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.RecordPasswordChangeAttemptsAsync([claimed]);

        var stored = await StoredAsync(id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(stored.AttemptCount, Is.EqualTo(1));
            Assert.That(stored.NextRetryAt, Is.EqualTo(AsOf.AddSeconds(2).AddMinutes(5)));
            Assert.That(stored.ClaimedAt, Is.Null);
            Assert.That(stored.ClaimedBy, Is.Null);
        }
    }

    /// <summary>
    /// The attempt write is guarded on the row still being Delivering. An administrator who cancelled the change
    /// while the directory was being written to must find it cancelled afterwards, not "JIM will try again".
    /// </summary>
    [Test]
    public async Task RecordPasswordChangeAttemptsAsync_AgainstARowCancelledMeanwhile_LeavesItCancelledAsync()
    {
        var systemId = await SeedSystemAsync();
        var id = await SeedChangeAsync(systemId);
        var claimed = (await ClaimAsync(systemId)).Single();

        await using (var cancel = NewContext())
        {
            var cancelled = await new PostgresDataRepository(cancel).Sync.CancelPasswordChangesAsync(
                new PendingPasswordChangeFilter { Ids = [id] }, Guid.NewGuid(), "Ada Lovelace", AsOf.AddSeconds(1));
            Assert.That(cancelled, Is.EqualTo(1), "A change being delivered can still be cancelled.");
        }

        claimed.RecordAttempt(PasswordSetFailureReason.Transient, "Server unavailable",
            new ConnectedSystemPasswordSynchronisation { MaxRetries = 3, RetryBackoffBase = TimeSpan.FromMinutes(5) }, AsOf.AddSeconds(2));
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.RecordPasswordChangeAttemptsAsync([claimed]);

        var stored = await StoredAsync(id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Cancelled));
            Assert.That(stored.CancelledByName, Is.EqualTo("Ada Lovelace"));
            Assert.That(stored.AttemptCount, Is.Zero, "The attempt landed nowhere; the cancellation stands.");
            Assert.That(stored.ClaimedBy, Is.Null);
        }
    }

    [Test]
    public async Task ReleasePasswordChangeClaimsAsync_ReturnsClaimedRowsToPendingUnattemptedAsync()
    {
        var systemId = await SeedSystemAsync();
        var id = await SeedChangeAsync(systemId);
        var claimed = await ClaimAsync(systemId);

        await using var ctx = NewContext();
        var released = await new PostgresDataRepository(ctx).Sync.ReleasePasswordChangeClaimsAsync(claimed.Select(c => c.Id));

        var stored = await StoredAsync(id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.EqualTo(1));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(stored.AttemptCount, Is.Zero);
            Assert.That(stored.ClaimedAt, Is.Null);
            Assert.That(stored.ClaimedBy, Is.Null);
            Assert.That(stored.IsDue(AsOf), Is.True);
        }
    }

    [Test]
    public async Task ReleasePasswordChangeClaimsAsync_LeavesARowCancelledMeanwhileAloneAsync()
    {
        var systemId = await SeedSystemAsync();
        var id = await SeedChangeAsync(systemId);
        await ClaimAsync(systemId);
        await using (var cancel = NewContext())
            await new PostgresDataRepository(cancel).Sync.CancelPasswordChangesAsync(
                new PendingPasswordChangeFilter { Ids = [id] }, null, null, AsOf.AddSeconds(1));

        await using var ctx = NewContext();
        var released = await new PostgresDataRepository(ctx).Sync.ReleasePasswordChangeClaimsAsync([id]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.Zero);
            Assert.That((await StoredAsync(id)).Status, Is.EqualTo(PendingPasswordChangeStatus.Cancelled));
        }
    }

    [Test]
    public async Task RetryPasswordChangesAsync_LeavesADeliveringRowAloneAsync()
    {
        // A change being delivered right now is already getting the attempt a retry asks for; resetting its
        // attempt count under the deliverer would make the outcome it records inconsistent.
        var systemId = await SeedSystemAsync();
        var id = await SeedChangeAsync(systemId);
        await ClaimAsync(systemId);

        await using var ctx = NewContext();
        var retried = await new PostgresDataRepository(ctx).Sync.RetryPasswordChangesAsync(new PendingPasswordChangeFilter { Ids = [id] });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retried, Is.Zero);
            Assert.That((await StoredAsync(id)).Status, Is.EqualTo(PendingPasswordChangeStatus.Delivering));
        }
    }

    [Test]
    public async Task DeleteTerminalPasswordChangesAsync_NeverRemovesADeliveringRowAsync()
    {
        var systemId = await SeedSystemAsync();
        var claimedId = await SeedChangeAsync(systemId, createdAt: AsOf.AddDays(-200));
        var parkedId = await SeedChangeAsync(systemId, c => c.Status = PendingPasswordChangeStatus.Parked, createdAt: AsOf.AddDays(-200));
        await ClaimAsync(systemId);

        await using var ctx = NewContext();
        var deleted = await new PostgresDataRepository(ctx).Sync.DeleteTerminalPasswordChangesAsync(AsOf.AddDays(-90), 100);

        await using var verify = NewContext();
        var remaining = await verify.PendingPasswordChanges.AsNoTracking().Select(c => c.Id).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted, Is.EqualTo(1));
            Assert.That(remaining, Is.EqualTo(new[] { claimedId }), $"The parked row {parkedId} goes; live work in a deliverer's hands stays.");
        }
    }

    [Test]
    public async Task GetPasswordQueueSummaryAsync_CountsADeliveringRowAsWaitingNotDueAsync()
    {
        var systemId = await SeedSystemAsync();
        await SeedChangeAsync(systemId);
        await SeedChangeAsync(systemId);
        await ClaimAsync(systemId, maximum: 1);

        await using var ctx = NewContext();
        var summary = await new PostgresDataRepository(ctx).Sync.GetPasswordQueueSummaryAsync(AsOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.WaitingCount, Is.EqualTo(2), "A claimed change is still work JIM intends to deliver.");
            Assert.That(summary.DueCount, Is.EqualTo(1), "It is being delivered, not waiting to be.");
        }
    }

    [Test]
    public async Task GetPasswordQueueDeliveryOutlookAsync_CountsDueAndRetryingAndNamesTheNextAttemptAsync()
    {
        var systemId = await SeedSystemAsync();
        await SeedChangeAsync(systemId);
        await SeedChangeAsync(systemId, c => c.NextRetryAt = AsOf.AddMinutes(-1));
        await SeedChangeAsync(systemId, c => c.NextRetryAt = AsOf.AddMinutes(7));
        await SeedChangeAsync(systemId, c => c.NextRetryAt = AsOf.AddMinutes(3));
        await SeedChangeAsync(systemId, c => c.Status = PendingPasswordChangeStatus.Parked);

        await using var ctx = NewContext();
        var outlook = await new PostgresDataRepository(ctx).Sync.GetPasswordQueueDeliveryOutlookAsync(AsOf, Lease);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outlook.DueCount, Is.EqualTo(2), "The never-attempted change and the retry that has come due.");
            Assert.That(outlook.RetryingCount, Is.EqualTo(2));
            Assert.That(outlook.NextAttemptAt, Is.EqualTo(AsOf.AddMinutes(3)));
        }
    }

    [Test]
    public async Task GetPasswordQueueDeliveryOutlookAsync_CountsAnExpiredClaimAsDueAsync()
    {
        var systemId = await SeedSystemAsync();
        await SeedChangeAsync(systemId);
        await ClaimAsync(systemId);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).Sync;
        var withinLease = await repository.GetPasswordQueueDeliveryOutlookAsync(AsOf + Lease - TimeSpan.FromSeconds(1), Lease);
        var afterLease = await repository.GetPasswordQueueDeliveryOutlookAsync(AsOf + Lease, Lease);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withinLease.DueCount, Is.Zero, "A change being delivered is not due.");
            Assert.That(afterLease.DueCount, Is.EqualTo(1), "A change whose deliverer has gone quiet is due again.");
        }
    }

    [Test]
    public async Task GetPasswordQueueDeliveryOutlookAsync_IgnoresASwitchedOffSystemAsync()
    {
        // A paused system's held changes must neither inflate the counts nor wake the service for retries it will
        // never make.
        var paused = await SeedSystemAsync("Contractor LDAP", enabled: false);
        await SeedChangeAsync(paused);
        await SeedChangeAsync(paused, c => c.NextRetryAt = AsOf.AddMinutes(1));

        await using var ctx = NewContext();
        var outlook = await new PostgresDataRepository(ctx).Sync.GetPasswordQueueDeliveryOutlookAsync(AsOf, Lease);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outlook.DueCount, Is.Zero);
            Assert.That(outlook.RetryingCount, Is.Zero);
            Assert.That(outlook.NextAttemptAt, Is.Null);
        }
    }

    [Test]
    public async Task GetPasswordQueueDeliveryOutlookAsync_EmptyQueue_IsAllZeroAsync()
    {
        await using var ctx = NewContext();
        var outlook = await new PostgresDataRepository(ctx).Sync.GetPasswordQueueDeliveryOutlookAsync(AsOf, Lease);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outlook.DueCount, Is.Zero);
            Assert.That(outlook.RetryingCount, Is.Zero);
            Assert.That(outlook.NextAttemptAt, Is.Null);
        }
    }

    [Test]
    public async Task GetPasswordChangesByActivityAsync_ReturnsOnlyThatChangesRowsAsync()
    {
        var first = await SeedSystemAsync("Corporate AD");
        var second = await SeedSystemAsync("HR Portal");
        var activityId = Guid.NewGuid();
        var a = await SeedChangeAsync(first, c => c.ActivityId = activityId);
        var b = await SeedChangeAsync(second, c => c.ActivityId = activityId);
        await SeedChangeAsync(first);

        await using var ctx = NewContext();
        var rows = await new PostgresDataRepository(ctx).Sync.GetPasswordChangesByActivityAsync(activityId);

        Assert.That(rows.Select(r => r.Id), Is.EquivalentTo(new[] { a, b }));
    }
}
