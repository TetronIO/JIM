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
/// Real-PostgreSQL verification of the Password Synchronisation queue (#1119).
/// <para>
/// Everything here is invisible to the in-memory provider by construction. The coalescing is an
/// <c>INSERT ... ON CONFLICT DO UPDATE</c> against a unique index, and the in-memory provider enforces no unique
/// constraints at all, so a race that would collide in production simply inserts twice there. The row itself is
/// written by raw SQL that bypasses the EF model, so a column added to the model but missed by the writer, or
/// written in the wrong position, persists as null or as the wrong value with no error anywhere.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c>
/// fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is absent. Do NOT run this fixture outside the sanctioned
/// scratch-database workflow: <c>SetUp</c> TRUNCATEs every table.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PasswordSynchronisationQueueDatabaseTests
{
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Password Synchronisation queue tests.");

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

    /// <summary>
    /// Every field survives the raw-SQL write path. The completeness test proves the column list matches the
    /// model; only a round trip proves the writer puts the right value in each column.
    /// </summary>
    [Test]
    public async Task QueuePasswordChangesAsync_PersistsEveryFieldAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var createdAt = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);
        var change = new PendingPasswordChange
        {
            MetaverseObjectId = mvoId,
            ConnectedSystemId = systemId,
            ConnectedSystemObjectId = csoId,
            EncryptedPassword = "$JIMPW$v1$ciphertext",
            ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn,
            Status = PendingPasswordChangeStatus.Pending,
            FailureReason = PasswordSetFailureReason.Transient,
            TargetMessage = "Server unavailable",
            AttemptCount = 2,
            NextRetryAt = createdAt.AddMinutes(10),
            CreatedAt = createdAt,
            LastAttemptedAt = createdAt.AddMinutes(1),
            ExpiresAt = createdAt.AddDays(7),
            ActivityId = Guid.NewGuid(),
            CancelledAt = createdAt.AddMinutes(2),
            CancelledById = Guid.NewGuid(),
            CancelledByName = "Ada Lovelace"
        };

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([change]);

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.MetaverseObjectId, Is.EqualTo(mvoId));
            Assert.That(stored.ConnectedSystemId, Is.EqualTo(systemId));
            Assert.That(stored.ConnectedSystemObjectId, Is.EqualTo(csoId));
            Assert.That(stored.EncryptedPassword, Is.EqualTo("$JIMPW$v1$ciphertext"));
            Assert.That(stored.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(stored.FailureReason, Is.EqualTo(PasswordSetFailureReason.Transient));
            Assert.That(stored.TargetMessage, Is.EqualTo("Server unavailable"));
            Assert.That(stored.AttemptCount, Is.EqualTo(2));
            Assert.That(stored.NextRetryAt, Is.EqualTo(change.NextRetryAt));
            Assert.That(stored.CreatedAt, Is.EqualTo(createdAt));
            Assert.That(stored.LastAttemptedAt, Is.EqualTo(change.LastAttemptedAt));
            Assert.That(stored.ExpiresAt, Is.EqualTo(change.ExpiresAt));
            Assert.That(stored.ActivityId, Is.EqualTo(change.ActivityId));
            Assert.That(stored.CancelledAt, Is.EqualTo(change.CancelledAt));
            Assert.That(stored.CancelledById, Is.EqualTo(change.CancelledById));
            Assert.That(stored.CancelledByName, Is.EqualTo("Ada Lovelace"));
        }
    }

    /// <summary>
    /// The whole point of the unique index: a second change for the same identity and system replaces the first
    /// rather than queueing behind it, so a password already superseded is never delivered.
    /// </summary>
    [Test]
    public async Task QueuePasswordChangesAsync_ForTheSameTargetTwice_CoalescesToOneRowAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var first = NewChange(mvoId, systemId, csoId, "$JIMPW$v1$first", new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc));
        var second = NewChange(mvoId, systemId, csoId, "$JIMPW$v1$second", new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc));

        await using (var write = NewContext())
        {
            var repository = new PostgresDataRepository(write).Sync;
            await repository.QueuePasswordChangesAsync([first]);
            await repository.QueuePasswordChangesAsync([second]);
        }

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.EncryptedPassword, Is.EqualTo("$JIMPW$v1$second"));
            Assert.That(stored.CreatedAt, Is.EqualTo(second.CreatedAt),
                "The expiry window runs from the newer change, not the one it replaced.");
            Assert.That(stored.ActivityId, Is.EqualTo(second.ActivityId));
        }
    }

    /// <summary>
    /// Superseding clears the attempt history along with the password it described. Carrying it forward would
    /// let a newer password inherit an exhausted retry budget, or a park earned by one nobody is delivering.
    /// </summary>
    [Test]
    public async Task QueuePasswordChangesAsync_SupersedingAParkedChange_ClearsItsFailureHistoryAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var parked = NewChange(mvoId, systemId, csoId, "$JIMPW$v1$first", DateTime.UtcNow);
        parked.Status = PendingPasswordChangeStatus.Parked;
        parked.FailureReason = PasswordSetFailureReason.PolicyRejection;
        parked.TargetMessage = "Password too short";
        parked.AttemptCount = 5;

        await using (var write = NewContext())
        {
            var repository = new PostgresDataRepository(write).Sync;
            await repository.QueuePasswordChangesAsync([parked]);
            await repository.QueuePasswordChangesAsync([NewChange(mvoId, systemId, csoId, "$JIMPW$v1$second", DateTime.UtcNow)]);
        }

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(stored.AttemptCount, Is.Zero);
            Assert.That(stored.FailureReason, Is.Null);
            Assert.That(stored.TargetMessage, Is.Null);
        }
    }

    /// <summary>
    /// Two changes for the same target inside one batch coalesce against each other, exactly as two separate
    /// calls do; a batched fan-out must not be able to insert a duplicate the unique index would refuse.
    /// </summary>
    [Test]
    public async Task QueuePasswordChangesAsync_WithTwoChangesForOneTargetInOneBatch_CoalescesAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([
                NewChange(mvoId, systemId, csoId, "$JIMPW$v1$first", DateTime.UtcNow),
                NewChange(mvoId, systemId, csoId, "$JIMPW$v1$second", DateTime.UtcNow)
            ]);

        await using var verify = NewContext();
        Assert.That(await verify.PendingPasswordChanges.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task GetDuePasswordChangesAsync_ReturnsOnlyPendingChangesThatHaveComeDueAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var now = DateTime.UtcNow;

        var dueNow = NewChange(mvoId, systemId, csoId, "$JIMPW$v1$due", now.AddHours(-1));
        var notYetDue = NewChange(await SeedIdentityAsync(), systemId, null, "$JIMPW$v1$waiting", now);
        notYetDue.NextRetryAt = now.AddHours(1);
        var parked = NewChange(await SeedIdentityAsync(), systemId, null, "$JIMPW$v1$parked", now);
        parked.Status = PendingPasswordChangeStatus.Parked;

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([dueNow, notYetDue, parked]);

        await using var read = NewContext();
        var due = await new PostgresDataRepository(read).Sync.GetDuePasswordChangesAsync(systemId, now, 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(due, Has.Count.EqualTo(1));
            Assert.That(due[0].EncryptedPassword, Is.EqualTo("$JIMPW$v1$due"));
        }
    }

    [Test]
    public async Task RecordPasswordChangeAttemptsAsync_PersistsTheOutcomeAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var change = NewChange(mvoId, systemId, null, "$JIMPW$v1$ciphertext", DateTime.UtcNow);

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([change]);

        // The account is resolved on the attempt, which is how a change queued before provisioning gains one.
        change.ConnectedSystemObjectId = csoId;
        change.Status = PendingPasswordChangeStatus.Parked;
        change.FailureReason = PasswordSetFailureReason.PolicyRejection;
        change.TargetMessage = "Password too short";
        change.AttemptCount = 1;
        change.LastAttemptedAt = DateTime.UtcNow;

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.RecordPasswordChangeAttemptsAsync([change]);

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.ConnectedSystemObjectId, Is.EqualTo(csoId));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
            Assert.That(stored.FailureReason, Is.EqualTo(PasswordSetFailureReason.PolicyRejection));
            Assert.That(stored.TargetMessage, Is.EqualTo("Password too short"));
            Assert.That(stored.AttemptCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ExpirePasswordChangesAsync_LeavesParkedChangesAloneAsync()
    {
        // A parked change is an administrator's to resolve; expiring it under them would remove the very thing
        // they were asked to look at.
        var (systemId, mvoId, _) = await SeedSystemIdentityAndAccountAsync();
        var now = DateTime.UtcNow;

        var overdue = NewChange(mvoId, systemId, null, "$JIMPW$v1$overdue", now.AddDays(-8));
        overdue.ExpiresAt = now.AddDays(-1);
        var parkedAndOverdue = NewChange(await SeedIdentityAsync(), systemId, null, "$JIMPW$v1$parked", now.AddDays(-8));
        parkedAndOverdue.ExpiresAt = now.AddDays(-1);
        parkedAndOverdue.Status = PendingPasswordChangeStatus.Parked;

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([overdue, parkedAndOverdue]);

        int expired;
        await using (var write = NewContext())
            expired = await new PostgresDataRepository(write).Sync.ExpirePasswordChangesAsync(systemId, now);

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().ToListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expired, Is.EqualTo(1));
            Assert.That(stored.Single(c => c.Id == overdue.Id).Status, Is.EqualTo(PendingPasswordChangeStatus.Expired));
            Assert.That(stored.Single(c => c.Id == parkedAndOverdue.Id).Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
        }
    }

    [Test]
    public async Task ReleasePasswordChangesForDeliveryAsync_ReleasesParkedButNotExpiredAsync()
    {
        // Drain-on-enable. An expired change is deliberately not released: its window passed, so the password it
        // carries may have been superseded by one JIM never saw, and delivering it would set a password the
        // person has already replaced.
        var (systemId, mvoId, _) = await SeedSystemIdentityAndAccountAsync();

        var parked = NewChange(mvoId, systemId, null, "$JIMPW$v1$parked", DateTime.UtcNow);
        parked.Status = PendingPasswordChangeStatus.Parked;
        parked.AttemptCount = 5;
        parked.FailureReason = PasswordSetFailureReason.ConfigurationFault;

        var expired = NewChange(await SeedIdentityAsync(), systemId, null, "$JIMPW$v1$expired", DateTime.UtcNow);
        expired.Status = PendingPasswordChangeStatus.Expired;

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([parked, expired]);

        int released;
        await using (var write = NewContext())
            released = await new PostgresDataRepository(write).Sync.ReleasePasswordChangesForDeliveryAsync(systemId);

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().ToListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.EqualTo(1));
            var releasedChange = stored.Single(c => c.Id == parked.Id);
            Assert.That(releasedChange.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(releasedChange.AttemptCount, Is.Zero);
            Assert.That(releasedChange.FailureReason, Is.Null);
            Assert.That(stored.Single(c => c.Id == expired.Id).Status, Is.EqualTo(PendingPasswordChangeStatus.Expired));
        }
    }

    [Test]
    public async Task DeleteTerminalPasswordChangesAsync_NeverRemovesLiveWorkAsync()
    {
        var (systemId, mvoId, _) = await SeedSystemIdentityAndAccountAsync();
        var longAgo = DateTime.UtcNow.AddDays(-200);

        var oldButPending = NewChange(mvoId, systemId, null, "$JIMPW$v1$pending", longAgo);
        var oldAndParked = NewChange(await SeedIdentityAsync(), systemId, null, "$JIMPW$v1$parked", longAgo);
        oldAndParked.Status = PendingPasswordChangeStatus.Parked;

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([oldButPending, oldAndParked]);

        int deleted;
        await using (var write = NewContext())
            deleted = await new PostgresDataRepository(write).Sync.DeleteTerminalPasswordChangesAsync(DateTime.UtcNow.AddDays(-90), 100);

        await using var verify = NewContext();
        var remaining = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted, Is.EqualTo(1));
            Assert.That(remaining.Id, Is.EqualTo(oldButPending.Id),
                "A change still being worked is never trimmed, however old.");
        }
    }

    [Test]
    public async Task DeletingAConnectedSystemObject_LeavesTheQueuedChangeBehindAsync()
    {
        // Set null rather than cascade: an account deleted and recreated must not take the password change with
        // it. The change re-resolves its account on the next attempt.
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([
                NewChange(mvoId, systemId, csoId, "$JIMPW$v1$ciphertext", DateTime.UtcNow)
            ]);

        await using (var delete = NewContext())
        {
            var cso = await delete.ConnectedSystemObjects.SingleAsync(c => c.Id == csoId);
            delete.ConnectedSystemObjects.Remove(cso);
            await delete.SaveChangesAsync();
        }

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        Assert.That(stored.ConnectedSystemObjectId, Is.Null);
    }

    private static PendingPasswordChange NewChange(Guid mvoId, int systemId, Guid? csoId, string ciphertext, DateTime createdAt) => new()
    {
        MetaverseObjectId = mvoId,
        ConnectedSystemId = systemId,
        ConnectedSystemObjectId = csoId,
        EncryptedPassword = ciphertext,
        CreatedAt = createdAt,
        ExpiresAt = createdAt.AddDays(7),
        ActivityId = Guid.NewGuid()
    };

    private async Task<(int SystemId, Guid MetaverseObjectId, Guid ConnectedSystemObjectId)> SeedSystemIdentityAndAccountAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true, SupportsPasswordSet = true };
        var system = new ConnectedSystem { Name = "Corporate AD", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = false };
        seed.AddRange(connectorDefinition, system, csType, mvType);
        await seed.SaveChangesAsync();

        var externalIdAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true,
            ConnectedSystemObjectType = csType
        };
        var mvo = new MetaverseObject { Type = mvType };
        seed.AddRange(externalIdAttribute, mvo);
        await seed.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            TypeId = csType.Id,
            ConnectedSystemId = system.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = externalIdAttribute.Id
        };
        seed.Add(cso);

        // Nothing reaches this queue for a system with no Password Synchronisation configuration, so a fixture
        // seeding changes without one is describing a state the application cannot produce. It matters to what
        // is read back: a change is due only where the system is enabled, because a delivery pass steps over a
        // switched-off one, so a configuration-less seed would report every change as waiting and none as due.
        seed.Add(new ConnectedSystemPasswordSynchronisation
        {
            ConnectedSystemId = system.Id,
            Enabled = true,
            TargetObjectTypeId = csType.Id
        });
        await seed.SaveChangesAsync();

        return (system.Id, mvo.Id, cso.Id);
    }

    private async Task<Guid> SeedIdentityAsync()
    {
        await using var seed = NewContext();
        var mvType = await seed.MetaverseObjectTypes.FirstAsync();
        var mvo = new MetaverseObject { Type = mvType };
        seed.Add(mvo);
        await seed.SaveChangesAsync();
        return mvo.Id;
    }

    /// <summary>
    /// The list projection resolves both names and, deliberately, has nowhere to carry the password.
    /// </summary>
    [Test]
    public async Task GetPendingPasswordChangeHeadersAsync_ResolvesNamesAndWindowsTheResultsAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var createdAt = new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc);

        await using (var write = NewContext())
        {
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([
                new PendingPasswordChange
                {
                    MetaverseObjectId = mvoId,
                    ConnectedSystemId = systemId,
                    ConnectedSystemObjectId = csoId,
                    EncryptedPassword = "$JIMPW$v1$ciphertext",
                    CreatedAt = createdAt,
                    ExpiresAt = createdAt.AddDays(7),
                    ActivityId = Guid.NewGuid()
                }
            ]);
        }

        await using var read = NewContext();
        var window = await new PostgresDataRepository(read).Sync.GetPendingPasswordChangeHeadersAsync(
            new PendingPasswordChangeFilter(), 0, 10, "queued", sortDescending: false, includeTotalCount: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.TotalResults, Is.EqualTo(1));
            Assert.That(window.Results, Has.Count.EqualTo(1));
            Assert.That(window.Results[0].ConnectedSystemName, Is.Not.Empty,
                "The Connected System's name is joined in, so a list can name it without a second query.");
            Assert.That(window.Results[0].MetaverseObjectId, Is.EqualTo(mvoId));
            Assert.That(window.Results[0].MetaverseObjectTypePluralName, Is.Not.Empty,
                "The Object Type's plural name is what a link to the identity is built from, and it is reached " +
                "through a navigation whose foreign key is a shadow property; only a real provider proves it translates.");
            Assert.That(window.Results[0].Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
        }
    }

    /// <summary>
    /// Counting is the expensive half of a window read, so a caller that already knows the total gets a null
    /// back rather than a second count. Null must not read as zero.
    /// </summary>
    [Test]
    public async Task GetPendingPasswordChangeHeadersAsync_WithoutTheTotal_ReturnsNullRatherThanZeroAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        await SeedChangeAsync(systemId, mvoId, csoId);

        await using var read = NewContext();
        var window = await new PostgresDataRepository(read).Sync.GetPendingPasswordChangeHeadersAsync(
            new PendingPasswordChangeFilter(), 0, 10, "queued", sortDescending: false, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Results, Has.Count.EqualTo(1));
            Assert.That(window.TotalResults, Is.Null);
        }
    }

    /// <summary>
    /// Retry is the way out of a park, and it must clear the failure that caused the park along with the attempt
    /// budget the park exhausted.
    /// </summary>
    [Test]
    public async Task RetryPasswordChangesAsync_ReleasesAParkedChangeAndClearsItsFailureAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var id = await SeedChangeAsync(systemId, mvoId, csoId, change =>
        {
            change.Status = PendingPasswordChangeStatus.Parked;
            change.AttemptCount = 5;
            change.FailureReason = PasswordSetFailureReason.PolicyRejection;
            change.TargetMessage = "Too short";
        });

        await using (var act = NewContext())
        {
            var affected = await new PostgresDataRepository(act).Sync.RetryPasswordChangesAsync(
                new PendingPasswordChangeFilter { Ids = [id] });
            Assert.That(affected, Is.EqualTo(1));
        }

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(stored.AttemptCount, Is.Zero);
            Assert.That(stored.FailureReason, Is.Null);
            Assert.That(stored.TargetMessage, Is.Null);
            Assert.That(stored.NextRetryAt, Is.Null);
        }
    }

    /// <summary>
    /// An expired change has no password left to send, so a retry that swept it up would queue an empty delivery.
    /// </summary>
    [Test]
    public async Task RetryPasswordChangesAsync_LeavesAnExpiredChangeExpiredAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var id = await SeedChangeAsync(systemId, mvoId, csoId, change => change.Status = PendingPasswordChangeStatus.Expired);

        await using (var act = NewContext())
        {
            var affected = await new PostgresDataRepository(act).Sync.RetryPasswordChangesAsync(
                new PendingPasswordChangeFilter { Ids = [id] });
            Assert.That(affected, Is.Zero);
        }

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();
        Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Expired));
    }

    /// <summary>
    /// Cancelling records who and when, and keeps the failure that stranded the change: why it was stuck is
    /// usually why it was cancelled.
    /// </summary>
    [Test]
    public async Task CancelPasswordChangesAsync_RecordsTheOutcomeAndItsAuthorAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var id = await SeedChangeAsync(systemId, mvoId, csoId, change =>
        {
            change.Status = PendingPasswordChangeStatus.Parked;
            change.FailureReason = PasswordSetFailureReason.PolicyRejection;
        });

        var administratorId = Guid.NewGuid();
        var cancelledAt = new DateTime(2026, 8, 21, 11, 0, 0, DateTimeKind.Utc);

        await using (var act = NewContext())
        {
            var affected = await new PostgresDataRepository(act).Sync.CancelPasswordChangesAsync(
                new PendingPasswordChangeFilter { Ids = [id] }, administratorId, "Ada Lovelace", cancelledAt);
            Assert.That(affected, Is.EqualTo(1));
        }

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Cancelled));
            Assert.That(stored.CancelledAt, Is.EqualTo(cancelledAt));
            Assert.That(stored.CancelledById, Is.EqualTo(administratorId));
            Assert.That(stored.CancelledByName, Is.EqualTo("Ada Lovelace"));
            Assert.That(stored.FailureReason, Is.EqualTo(PasswordSetFailureReason.PolicyRejection));
        }
    }

    /// <summary>
    /// Cancelling something already finished would overwrite the outcome that actually happened to it.
    /// </summary>
    [Test]
    public async Task CancelPasswordChangesAsync_LeavesAnExpiredChangeAloneAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var id = await SeedChangeAsync(systemId, mvoId, csoId, change => change.Status = PendingPasswordChangeStatus.Expired);

        await using (var act = NewContext())
        {
            var affected = await new PostgresDataRepository(act).Sync.CancelPasswordChangesAsync(
                new PendingPasswordChangeFilter { Ids = [id] }, Guid.NewGuid(), "Ada Lovelace", DateTime.UtcNow);
            Assert.That(affected, Is.Zero);
        }

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Expired));
            Assert.That(stored.CancelledAt, Is.Null);
        }
    }

    /// <summary>
    /// The summary counts every state, including the two an administrator produced. Due is reported apart from
    /// waiting because a queue working through its backoffs and a queue nobody is draining look identical
    /// otherwise.
    /// </summary>
    [Test]
    public async Task GetPasswordQueueSummaryAsync_CountsEveryStateAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var asOf = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

        await SeedChangeAsync(systemId, mvoId, csoId);
        var (_, secondMvo, secondCso) = await SeedSystemIdentityAndAccountAsync();
        await SeedChangeAsync(systemId, secondMvo, secondCso, change =>
        {
            change.Status = PendingPasswordChangeStatus.Pending;
            change.NextRetryAt = asOf.AddHours(2);
        });

        await using var read = NewContext();
        var summary = await new PostgresDataRepository(read).Sync.GetPasswordQueueSummaryAsync(asOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.WaitingCount, Is.EqualTo(2));
            Assert.That(summary.DueCount, Is.EqualTo(1),
                "The change waiting out a backoff is waiting, but not due.");
            Assert.That(summary.ParkedCount, Is.Zero);
            Assert.That(summary.ExpiredCount, Is.Zero);
            Assert.That(summary.CancelledCount, Is.Zero);
        }
    }

    /// <summary>
    /// A change queued for a Connected System that is switched off is waiting but not due. Delivery steps over
    /// that system without touching its changes, so counting them as due would make the ordinary state of a
    /// deployment with one system off (requirement 2's accumulate) look like a queue nothing is draining, which
    /// is precisely the reading the two counts exist to separate.
    /// </summary>
    [Test]
    public async Task GetPasswordQueueSummaryAsync_ASwitchedOffSystemIsWaitingNotDueAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var asOf = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

        await SeedChangeAsync(systemId, mvoId, csoId);

        await using (var disable = NewContext())
        {
            var configuration = await disable.ConnectedSystemPasswordSynchronisations
                .SingleAsync(ps => ps.ConnectedSystemId == systemId);
            configuration.Enabled = false;
            await disable.SaveChangesAsync();
        }

        await using var read = NewContext();
        var summary = await new PostgresDataRepository(read).Sync.GetPasswordQueueSummaryAsync(asOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.WaitingCount, Is.EqualTo(1),
                "the change is still owed to that system, and switching it on is what delivers it");
            Assert.That(summary.DueCount, Is.Zero,
                "a delivery pass would step over the system, so nothing about this change is due");
        }
    }

    /// <summary>
    /// Seeds one queued change, optionally adjusted, and returns its identifier.
    /// </summary>
    private async Task<Guid> SeedChangeAsync(
        int systemId,
        Guid mvoId,
        Guid csoId,
        Action<PendingPasswordChange>? adjust = null)
    {
        var change = new PendingPasswordChange
        {
            MetaverseObjectId = mvoId,
            ConnectedSystemId = systemId,
            ConnectedSystemObjectId = csoId,
            EncryptedPassword = "$JIMPW$v1$ciphertext",
            CreatedAt = new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc),
            ExpiresAt = new DateTime(2026, 8, 28, 9, 0, 0, DateTimeKind.Utc),
            ActivityId = Guid.NewGuid()
        };

        adjust?.Invoke(change);

        await using var write = NewContext();
        await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([change]);
        return change.Id;
    }
}
