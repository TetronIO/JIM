// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.PostgresData;
using JIM.TestSupport;
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
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    /// <summary>
    /// Every field survives the raw-SQL write path. The completeness test proves the column list matches the
    /// model; only a round trip proves the writer puts the right value in each column.
    /// </summary>
    [Test]
    public async Task QueuePasswordChangesAsync_PersistsEveryFieldAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var syncRuleId = await SeedSyncRuleAsync(systemId);
        var createdAt = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);
        var change = new PendingPasswordChange
        {
            MetaverseObjectId = mvoId,
            ConnectedSystemId = systemId,
            ConnectedSystemObjectId = csoId,
            // Null rather than a ciphertext, matching a Provisioned row: its password is resolved from
            // SyncRuleId's settings at each attempt, never stored on the row (#1697).
            EncryptedPassword = null,
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
            CancelledByName = "Ada Lovelace",
            ClaimedAt = createdAt.AddMinutes(3),
            ClaimedBy = "worker-1a2b3c4d",
            SyncRuleId = syncRuleId
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
            Assert.That(stored.EncryptedPassword, Is.Null);
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
            Assert.That(stored.ClaimedAt, Is.EqualTo(change.ClaimedAt));
            Assert.That(stored.ClaimedBy, Is.EqualTo("worker-1a2b3c4d"));
            Assert.That(stored.SyncRuleId, Is.EqualTo(syncRuleId));
        }
    }

    /// <summary>
    /// An administrator's own set replaces a provisioned row's link to the rule that generated it: the
    /// administrator's password has nothing left to generate from (#1697).
    /// </summary>
    [Test]
    public async Task QueuePasswordChangesAsync_ExplicitSetOverAProvisionedRow_ClearsSyncRuleIdAndCarriesThePasswordAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var syncRuleId = await SeedSyncRuleAsync(systemId);

        var provisioned = NewProvisionedChange(mvoId, systemId, csoId, syncRuleId, Guid.NewGuid(), DateTime.UtcNow);
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([provisioned]);

        var explicitSet = new PendingPasswordChange
        {
            MetaverseObjectId = mvoId,
            ConnectedSystemId = systemId,
            ConnectedSystemObjectId = csoId,
            EncryptedPassword = "$JIMPW$v1$administrator-set",
            Origin = PendingPasswordChangeOrigin.Explicit,
            EnableAccount = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(1),
            ExpiresAt = DateTime.UtcNow.AddMinutes(1).AddDays(7),
            ActivityId = Guid.NewGuid()
        };
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([explicitSet]);

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Explicit));
            Assert.That(stored.SyncRuleId, Is.Null, "The administrator's own password has nothing left to generate from.");
            Assert.That(stored.EncryptedPassword, Is.EqualTo("$JIMPW$v1$administrator-set"));
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
        var queued = NewChange(mvoId, systemId, null, "$JIMPW$v1$ciphertext", DateTime.UtcNow);

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([queued]);

        // An attempt is only ever recorded against a claimed row (#1635): the write is guarded on the row still
        // being Delivering, so the lane's claim is what makes it land.
        PendingPasswordChange change;
        await using (var claim = NewContext())
            change = (await new PostgresDataRepository(claim).Sync.ClaimDuePasswordChangesAsync(
                systemId, "worker-1a2b3c4d", DateTime.UtcNow, PendingPasswordChange.ClaimLease, 10, excludePropagated: false)).Single();

        // The account is resolved on the attempt, which is how a change queued before provisioning gains one; the
        // outcome goes through the model's own transition, as the lane's does, so the claim ends with it.
        change.ConnectedSystemObjectId = csoId;
        change.RecordAttempt(PasswordSetFailureReason.PolicyRejection, "Password too short",
            new ConnectedSystemPasswordSynchronisation { MaxRetries = 3, RetryBackoffBase = TimeSpan.FromMinutes(5) }, DateTime.UtcNow);

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
            Assert.That(stored.ClaimedBy, Is.Null, "An attempt ends the claim.");
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
            expired = await new PostgresDataRepository(write).Sync.ExpirePasswordChangesAsync(systemId, now, excludePropagated: false);

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

    private async Task<(int SystemId, Guid MetaverseObjectId, Guid ConnectedSystemObjectId)> SeedSystemIdentityAndAccountAsync(string name = "Corporate AD")
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = $"{name} Connector", BuiltIn = true, SupportsPasswordSet = true };
        var system = new ConnectedSystem { Name = name, ConnectorDefinition = connectorDefinition };
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
    /// A provisioning Synchronisation Rule over an already-seeded Connected System, following the same pattern
    /// as <c>InitialPasswordProvisioningDatabaseTests.SeedSystemAndRuleAsync</c>. Returns the rule's id.
    /// </summary>
    private async Task<int> SeedSyncRuleAsync(int systemId)
    {
        await using var seed = NewContext();
        var csType = await seed.ConnectedSystemObjectTypes.SingleAsync(t => t.ConnectedSystemId == systemId);
        var mvType = await seed.MetaverseObjectTypes.FirstAsync();

        var syncRule = new SyncRule
        {
            Name = "Provision Users",
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = systemId,
            ConnectedSystemObjectTypeId = csType.Id,
            MetaverseObjectTypeId = mvType.Id,
            ProvisionToConnectedSystem = true
        };
        seed.SyncRules.Add(syncRule);
        await seed.SaveChangesAsync();
        return syncRule.Id;
    }

    /// <summary>
    /// A second Connected System Object on an already-seeded system, for the "account deleted and re-provisioned"
    /// staging scenario: the same identity and system, a different account.
    /// </summary>
    private async Task<Guid> SeedAdditionalConnectedSystemObjectAsync(int systemId)
    {
        await using var seed = NewContext();
        var csType = await seed.ConnectedSystemObjectTypes.SingleAsync(t => t.ConnectedSystemId == systemId);
        var externalIdAttribute = await seed.ConnectedSystemAttributes.SingleAsync(a => a.ConnectedSystemObjectType.Id == csType.Id);

        var cso = new ConnectedSystemObject
        {
            TypeId = csType.Id,
            ConnectedSystemId = systemId,
            Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = externalIdAttribute.Id
        };
        seed.Add(cso);
        await seed.SaveChangesAsync();
        return cso.Id;
    }

    /// <summary>
    /// A Provisioned change ready to offer to <c>StageProvisionedPasswordChangesAsync</c>: no password value, the
    /// provisioning rule set, everything else the caller wants to vary.
    /// </summary>
    private static PendingPasswordChange NewProvisionedChange(Guid mvoId, int systemId, Guid csoId, int syncRuleId, Guid activityId, DateTime createdAt) => new()
    {
        MetaverseObjectId = mvoId,
        ConnectedSystemId = systemId,
        ConnectedSystemObjectId = csoId,
        EncryptedPassword = null,
        Origin = PendingPasswordChangeOrigin.Provisioned,
        SyncRuleId = syncRuleId,
        CreatedAt = createdAt,
        ExpiresAt = createdAt.AddDays(7),
        ActivityId = activityId
    };

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
    /// The Waiting filter (Pending) also returns a change the Password Delivery Service has claimed (#1635). The
    /// summary counts a Delivering row as waiting, so a list filtered to waiting that dropped it would show one
    /// fewer row than the card above it says there are.
    /// </summary>
    [Test]
    public async Task GetPendingPasswordChangeHeadersAsync_WaitingFilter_IncludesAChangeBeingDeliveredAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        await SeedChangeAsync(systemId, mvoId, csoId, change => change.Status = PendingPasswordChangeStatus.Delivering);

        await using var read = NewContext();
        var window = await new PostgresDataRepository(read).Sync.GetPendingPasswordChangeHeadersAsync(
            new PendingPasswordChangeFilter { Status = PendingPasswordChangeStatus.Pending }, 0, 10, "queued", sortDescending: false, includeTotalCount: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.TotalResults, Is.EqualTo(1));
            Assert.That(window.Results, Has.Count.EqualTo(1));
            Assert.That(window.Results[0].Status, Is.EqualTo(PendingPasswordChangeStatus.Delivering),
                "the row keeps its own status; only the filter's reach is wider");
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

    #region staging provisioned passwords (#1697)

    /// <summary>
    /// The raw-SQL write path's own round trip: every field a Provisioned row can carry survives, including the
    /// null <see cref="PendingPasswordChange.EncryptedPassword"/> and the provisioning rule.
    /// </summary>
    [Test]
    public async Task StageProvisionedPasswordChangesAsync_RoundTripsEveryFieldAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var syncRuleId = await SeedSyncRuleAsync(systemId);
        var createdAt = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var change = new PendingPasswordChange
        {
            MetaverseObjectId = mvoId,
            ConnectedSystemId = systemId,
            ConnectedSystemObjectId = csoId,
            EncryptedPassword = null,
            ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn,
            Status = PendingPasswordChangeStatus.Pending,
            Origin = PendingPasswordChangeOrigin.Provisioned,
            SyncRuleId = syncRuleId,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(7),
            ActivityId = Guid.NewGuid()
        };

        List<ProvisionedPasswordStagingOutcome> outcomes;
        await using (var write = NewContext())
            outcomes = await new PostgresDataRepository(write).Sync.StageProvisionedPasswordChangesAsync([change]);

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes, Has.Count.EqualTo(1));
            Assert.That(outcomes[0].Disposition, Is.EqualTo(ProvisionedPasswordStagingDisposition.Inserted));
            Assert.That(outcomes[0].RowId, Is.EqualTo(change.Id));
            Assert.That(outcomes[0].RequestedId, Is.EqualTo(change.Id));
            Assert.That(stored.MetaverseObjectId, Is.EqualTo(mvoId));
            Assert.That(stored.ConnectedSystemId, Is.EqualTo(systemId));
            Assert.That(stored.ConnectedSystemObjectId, Is.EqualTo(csoId));
            Assert.That(stored.EncryptedPassword, Is.Null);
            Assert.That(stored.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(stored.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Provisioned));
            Assert.That(stored.SyncRuleId, Is.EqualTo(syncRuleId));
            Assert.That(stored.CreatedAt, Is.EqualTo(createdAt));
            Assert.That(stored.ExpiresAt, Is.EqualTo(change.ExpiresAt));
            Assert.That(stored.ActivityId, Is.EqualTo(change.ActivityId));
        }
    }

    /// <summary>
    /// A pending propagated change is the person's real password, still on its way; the provisioned first
    /// password loses, and nothing is written. The wait is released anyway, because the account it was waiting
    /// for now exists.
    /// </summary>
    [Test]
    public async Task StageProvisionedPasswordChangesAsync_ConflictWithAPendingPropagatedRow_LeavesItAndMakesItDueNowAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var syncRuleId = await SeedSyncRuleAsync(systemId);
        var existing = new PendingPasswordChange
        {
            MetaverseObjectId = mvoId,
            ConnectedSystemId = systemId,
            ConnectedSystemObjectId = null,
            EncryptedPassword = "$JIMPW$v1$propagated",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            ActivityId = Guid.NewGuid(),
            NextRetryAt = DateTime.UtcNow.AddMinutes(10)
        };
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([existing]);

        var provisioned = NewProvisionedChange(mvoId, systemId, csoId, syncRuleId, Guid.NewGuid(), DateTime.UtcNow);

        List<ProvisionedPasswordStagingOutcome> outcomes;
        await using (var write = NewContext())
            outcomes = await new PostgresDataRepository(write).Sync.StageProvisionedPasswordChangesAsync([provisioned]);

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Disposition, Is.EqualTo(ProvisionedPasswordStagingDisposition.Coalesced));
            Assert.That(outcomes[0].RowId, Is.EqualTo(Guid.Empty));
            Assert.That(stored.Id, Is.EqualTo(existing.Id), "The account's real password wins; nothing about it changes identity.");
            Assert.That(stored.EncryptedPassword, Is.EqualTo("$JIMPW$v1$propagated"));
            Assert.That(stored.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Propagated));
            Assert.That(stored.NextRetryAt, Is.Null, "Released to run now that the account it was waiting for exists.");
        }
    }

    /// <summary>
    /// A parked row is an administrator's to resolve, not a wait to release: it carries no scheduled retry to
    /// clear, so this conflict leaves it exactly as it was.
    /// </summary>
    [Test]
    public async Task StageProvisionedPasswordChangesAsync_ConflictWithAParkedRow_LeavesItAloneAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var syncRuleId = await SeedSyncRuleAsync(systemId);
        var existing = new PendingPasswordChange
        {
            MetaverseObjectId = mvoId,
            ConnectedSystemId = systemId,
            ConnectedSystemObjectId = csoId,
            EncryptedPassword = "$JIMPW$v1$parked",
            Origin = PendingPasswordChangeOrigin.Explicit,
            Status = PendingPasswordChangeStatus.Parked,
            FailureReason = PasswordSetFailureReason.PolicyRejection,
            TargetMessage = "Password too short",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            ActivityId = Guid.NewGuid()
        };
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([existing]);

        var provisioned = NewProvisionedChange(mvoId, systemId, csoId, syncRuleId, Guid.NewGuid(), DateTime.UtcNow);

        List<ProvisionedPasswordStagingOutcome> outcomes;
        await using (var write = NewContext())
            outcomes = await new PostgresDataRepository(write).Sync.StageProvisionedPasswordChangesAsync([provisioned]);

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Disposition, Is.EqualTo(ProvisionedPasswordStagingDisposition.Coalesced));
            Assert.That(outcomes[0].RowId, Is.EqualTo(Guid.Empty));
            Assert.That(stored.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
            Assert.That(stored.NextRetryAt, Is.Null, "Untouched: there was nothing scheduled to clear.");
            Assert.That(stored.TargetMessage, Is.EqualTo("Password too short"));
        }
    }

    /// <summary>
    /// An expired row carries a dead password; the provisioned first password is what the account gets instead.
    /// The row keeps its own id, so a caller must adopt it for anything it does with the change afterwards.
    /// </summary>
    [Test]
    public async Task StageProvisionedPasswordChangesAsync_ConflictWithAnExpiredRow_SupersedesAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var syncRuleId = await SeedSyncRuleAsync(systemId);
        var existing = new PendingPasswordChange
        {
            MetaverseObjectId = mvoId,
            ConnectedSystemId = systemId,
            ConnectedSystemObjectId = csoId,
            EncryptedPassword = "$JIMPW$v1$stale",
            Status = PendingPasswordChangeStatus.Expired,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            ExpiresAt = DateTime.UtcNow.AddDays(-3),
            ActivityId = Guid.NewGuid()
        };
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([existing]);

        var provisionedActivityId = Guid.NewGuid();
        var provisioned = NewProvisionedChange(mvoId, systemId, csoId, syncRuleId, provisionedActivityId, DateTime.UtcNow);
        var requestedId = provisioned.Id;

        List<ProvisionedPasswordStagingOutcome> outcomes;
        await using (var write = NewContext())
            outcomes = await new PostgresDataRepository(write).Sync.StageProvisionedPasswordChangesAsync([provisioned]);

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Disposition, Is.EqualTo(ProvisionedPasswordStagingDisposition.Superseded));
            Assert.That(outcomes[0].RowId, Is.EqualTo(existing.Id));
            Assert.That(outcomes[0].RequestedId, Is.EqualTo(requestedId));
            Assert.That(provisioned.Id, Is.EqualTo(existing.Id), "The caller must hold the row's id, not the one it offered.");
            Assert.That(stored.Id, Is.EqualTo(existing.Id));
            Assert.That(stored.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Provisioned));
            Assert.That(stored.ActivityId, Is.EqualTo(provisionedActivityId));
            Assert.That(stored.EncryptedPassword, Is.Null);
        }
    }

    /// <summary>
    /// The account was deleted and re-provisioned: an existing Provisioned row means there is nothing worth
    /// keeping, so the new row's account and Activity replace it.
    /// </summary>
    [Test]
    public async Task StageProvisionedPasswordChangesAsync_ConflictWithAProvisionedRow_SupersedesAsync()
    {
        var (systemId, mvoId, firstCsoId) = await SeedSystemIdentityAndAccountAsync();
        var syncRuleId = await SeedSyncRuleAsync(systemId);
        var existing = NewProvisionedChange(mvoId, systemId, firstCsoId, syncRuleId, Guid.NewGuid(), DateTime.UtcNow.AddDays(-1));
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([existing]);

        var newCsoId = await SeedAdditionalConnectedSystemObjectAsync(systemId);
        var newActivityId = Guid.NewGuid();
        var provisionedAgain = NewProvisionedChange(mvoId, systemId, newCsoId, syncRuleId, newActivityId, DateTime.UtcNow);

        List<ProvisionedPasswordStagingOutcome> outcomes;
        await using (var write = NewContext())
            outcomes = await new PostgresDataRepository(write).Sync.StageProvisionedPasswordChangesAsync([provisionedAgain]);

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Disposition, Is.EqualTo(ProvisionedPasswordStagingDisposition.Superseded));
            Assert.That(stored.ConnectedSystemObjectId, Is.EqualTo(newCsoId), "the re-provisioned account replaces the deleted one");
            Assert.That(stored.ActivityId, Is.EqualTo(newActivityId));
        }
    }

    /// <summary>
    /// One statement per row, and one outcome per row in the order offered: a batch mixing all three dispositions
    /// must not let one row's outcome bleed into another's, or reorder what the caller gets back.
    /// </summary>
    [Test]
    public async Task StageProvisionedPasswordChangesAsync_ReturnsInsertedSupersededAndCoalescedPerRowAsync()
    {
        var (systemId, mvoA, csoId) = await SeedSystemIdentityAndAccountAsync();
        var syncRuleId = await SeedSyncRuleAsync(systemId);
        var mvoB = await SeedIdentityAsync();
        var mvoC = await SeedIdentityAsync();

        var expiredExisting = new PendingPasswordChange
        {
            MetaverseObjectId = mvoB,
            ConnectedSystemId = systemId,
            ConnectedSystemObjectId = csoId,
            Status = PendingPasswordChangeStatus.Expired,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            ExpiresAt = DateTime.UtcNow.AddDays(-3),
            ActivityId = Guid.NewGuid()
        };
        var pendingExplicitExisting = new PendingPasswordChange
        {
            MetaverseObjectId = mvoC,
            ConnectedSystemId = systemId,
            ConnectedSystemObjectId = csoId,
            EncryptedPassword = "$JIMPW$v1$real",
            Origin = PendingPasswordChangeOrigin.Explicit,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            ActivityId = Guid.NewGuid()
        };
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([expiredExisting, pendingExplicitExisting]);

        var toInsert = NewProvisionedChange(mvoA, systemId, csoId, syncRuleId, Guid.NewGuid(), DateTime.UtcNow);
        var toSupersede = NewProvisionedChange(mvoB, systemId, csoId, syncRuleId, Guid.NewGuid(), DateTime.UtcNow);
        var toCoalesce = NewProvisionedChange(mvoC, systemId, csoId, syncRuleId, Guid.NewGuid(), DateTime.UtcNow);

        List<ProvisionedPasswordStagingOutcome> outcomes;
        await using (var write = NewContext())
            outcomes = await new PostgresDataRepository(write).Sync.StageProvisionedPasswordChangesAsync([toInsert, toSupersede, toCoalesce]);

        Assert.That(outcomes.Select(o => o.Disposition), Is.EqualTo(new[]
        {
            ProvisionedPasswordStagingDisposition.Inserted,
            ProvisionedPasswordStagingDisposition.Superseded,
            ProvisionedPasswordStagingDisposition.Coalesced
        }), "One outcome per row, in the order offered.");
    }

    /// <summary>
    /// Deleting the provisioning Synchronisation Rule must not erase the fact that an account is still owed a
    /// first password; that is a fact about the account, not about the rule.
    /// </summary>
    [Test]
    public async Task DeletingTheSyncRule_NullsSyncRuleIdAndKeepsTheRowAsync()
    {
        var (systemId, mvoId, csoId) = await SeedSystemIdentityAndAccountAsync();
        var syncRuleId = await SeedSyncRuleAsync(systemId);
        var change = NewProvisionedChange(mvoId, systemId, csoId, syncRuleId, Guid.NewGuid(), DateTime.UtcNow);

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.StageProvisionedPasswordChangesAsync([change]);

        await using (var delete = NewContext())
        {
            delete.SyncRules.Remove(await delete.SyncRules.SingleAsync(sr => sr.Id == syncRuleId));
            await delete.SaveChangesAsync();
        }

        await using var verify = NewContext();
        var stored = await verify.PendingPasswordChanges.AsNoTracking().SingleOrDefaultAsync(c => c.Id == change.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored, Is.Not.Null, "deleting the rule must not delete the account's outstanding first password");
            Assert.That(stored!.SyncRuleId, Is.Null);
        }
    }

    /// <summary>
    /// The Activity write path detaches what it persists, so the run-long Worker context does not accumulate one
    /// entry per delivered password over the course of a run.
    /// </summary>
    [Test]
    public async Task CreateActivitiesAsync_PersistsCompletedSystemAttributedActivitiesAsync()
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystem,
            Status = ActivityStatus.Complete,
            InitiatedByType = ActivityInitiatorType.System,
            Message = "Provisioned password delivered"
        };

        await using var write = NewContext();
        await new PostgresDataRepository(write).Sync.CreateActivitiesAsync([activity]);

        Assert.That(write.ChangeTracker.Entries().Count(), Is.Zero, "the run-long context must not accumulate these");

        await using var verify = NewContext();
        var stored = await verify.Activities.AsNoTracking().SingleAsync(a => a.Id == activity.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(stored.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
            Assert.That(stored.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystem));
        }
    }

    /// <summary>
    /// Releasing is scoped to the one rule's Provisioned rows: another rule's parked row, and a parked Explicit
    /// row on the same system, are both none of this release's business.
    /// </summary>
    [Test]
    public async Task ReleaseParkedProvisionedPasswordChangesAsync_ReleasesOnlyThatRulesProvisionedRowsAsync()
    {
        var (systemA, mvoA, csoA) = await SeedSystemIdentityAndAccountAsync("System A");
        var ruleA = await SeedSyncRuleAsync(systemA);
        var (systemB, mvoB, csoB) = await SeedSystemIdentityAndAccountAsync("System B");
        var ruleB = await SeedSyncRuleAsync(systemB);
        var mvoC = await SeedIdentityAsync();

        var releasable = new PendingPasswordChange
        {
            MetaverseObjectId = mvoA,
            ConnectedSystemId = systemA,
            ConnectedSystemObjectId = csoA,
            Origin = PendingPasswordChangeOrigin.Provisioned,
            SyncRuleId = ruleA,
            Status = PendingPasswordChangeStatus.Parked,
            FailureReason = PasswordSetFailureReason.PolicyRejection,
            TargetMessage = "Password too short",
            AttemptCount = 3,
            ClaimedAt = DateTime.UtcNow,
            ClaimedBy = "worker-a",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            ActivityId = Guid.NewGuid()
        };
        var otherRulesRow = new PendingPasswordChange
        {
            MetaverseObjectId = mvoB,
            ConnectedSystemId = systemB,
            ConnectedSystemObjectId = csoB,
            Origin = PendingPasswordChangeOrigin.Provisioned,
            SyncRuleId = ruleB,
            Status = PendingPasswordChangeStatus.Parked,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            ActivityId = Guid.NewGuid()
        };
        var explicitOnSameSystem = new PendingPasswordChange
        {
            MetaverseObjectId = mvoC,
            ConnectedSystemId = systemA,
            Origin = PendingPasswordChangeOrigin.Explicit,
            Status = PendingPasswordChangeStatus.Parked,
            EncryptedPassword = "$JIMPW$v1$explicit",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            ActivityId = Guid.NewGuid()
        };

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([releasable, otherRulesRow, explicitOnSameSystem]);

        await using var ctx = NewContext();
        var released = await new PostgresDataRepository(ctx).Sync.ReleaseParkedProvisionedPasswordChangesAsync(ruleA);

        await using var verify = NewContext();
        var releasedRow = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync(c => c.Id == releasable.Id);
        var otherRuleRow = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync(c => c.Id == otherRulesRow.Id);
        var explicitRow = await verify.PendingPasswordChanges.AsNoTracking().SingleAsync(c => c.Id == explicitOnSameSystem.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.EqualTo(1));
            Assert.That(releasedRow.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(releasedRow.AttemptCount, Is.Zero);
            Assert.That(releasedRow.NextRetryAt, Is.Null);
            Assert.That(releasedRow.FailureReason, Is.Null);
            Assert.That(releasedRow.TargetMessage, Is.Null);
            Assert.That(releasedRow.ClaimedAt, Is.Null);
            Assert.That(releasedRow.ClaimedBy, Is.Null);
            Assert.That(otherRuleRow.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked), "a different rule's own parked row is not this rule's to release");
            Assert.That(explicitRow.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked), "an administrator's own set is not provisioning work");
        }
    }

    /// <summary>
    /// Parked and expired are reported apart: one is fixed by correcting the rule's settings, the other cannot
    /// be fixed there at all. A rule with neither is absent rather than present with zeroes.
    /// </summary>
    [Test]
    public async Task GetProvisionedPasswordAttentionBySyncRuleAsync_CountsParkedAndExpiredSeparatelyAsync()
    {
        var (systemId, mvoA, csoId) = await SeedSystemIdentityAndAccountAsync();
        var ruleWithAttention = await SeedSyncRuleAsync(systemId);
        var ruleSettled = await SeedSyncRuleAsync(systemId);
        var mvoB = await SeedIdentityAsync();
        var mvoC = await SeedIdentityAsync();

        var parked = new PendingPasswordChange
        {
            MetaverseObjectId = mvoA,
            ConnectedSystemId = systemId,
            ConnectedSystemObjectId = csoId,
            Origin = PendingPasswordChangeOrigin.Provisioned,
            SyncRuleId = ruleWithAttention,
            Status = PendingPasswordChangeStatus.Parked,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            ActivityId = Guid.NewGuid()
        };
        var expired = new PendingPasswordChange
        {
            MetaverseObjectId = mvoB,
            ConnectedSystemId = systemId,
            ConnectedSystemObjectId = csoId,
            Origin = PendingPasswordChangeOrigin.Provisioned,
            SyncRuleId = ruleWithAttention,
            Status = PendingPasswordChangeStatus.Expired,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            ExpiresAt = DateTime.UtcNow.AddDays(-3),
            ActivityId = Guid.NewGuid()
        };
        var settledElsewhere = new PendingPasswordChange
        {
            MetaverseObjectId = mvoC,
            ConnectedSystemId = systemId,
            ConnectedSystemObjectId = csoId,
            Origin = PendingPasswordChangeOrigin.Provisioned,
            SyncRuleId = ruleSettled,
            Status = PendingPasswordChangeStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            ActivityId = Guid.NewGuid()
        };

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync([parked, expired, settledElsewhere]);

        await using var ctx = NewContext();
        var attention = await new PostgresDataRepository(ctx).Sync.GetProvisionedPasswordAttentionBySyncRuleAsync([ruleWithAttention, ruleSettled]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attention[ruleWithAttention].ParkedCount, Is.EqualTo(1));
            Assert.That(attention[ruleWithAttention].ExpiredCount, Is.EqualTo(1));
            Assert.That(attention.ContainsKey(ruleSettled), Is.False, "a settled rule reports nothing rather than zeroes");
        }
    }

    /// <summary>
    /// Rejection reasons are grouped by message, biggest group first: an administrator is fixing a setting, not
    /// reading a list of accounts.
    /// </summary>
    [Test]
    public async Task GetParkedProvisionedPasswordReasonsAsync_GroupsByMessageBiggestFirstAsync()
    {
        var (systemId, mvoA, csoId) = await SeedSystemIdentityAndAccountAsync();
        var ruleId = await SeedSyncRuleAsync(systemId);
        var mvoB = await SeedIdentityAsync();
        var mvoC = await SeedIdentityAsync();

        var earliestTooShort = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var laterTooShort = new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
        var policyViolationSeenAt = new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);

        var changes = new[]
        {
            new PendingPasswordChange
            {
                MetaverseObjectId = mvoA,
                ConnectedSystemId = systemId,
                ConnectedSystemObjectId = csoId,
                Origin = PendingPasswordChangeOrigin.Provisioned,
                SyncRuleId = ruleId,
                Status = PendingPasswordChangeStatus.Parked,
                FailureReason = PasswordSetFailureReason.PolicyRejection,
                TargetMessage = "Password too short",
                LastAttemptedAt = earliestTooShort,
                CreatedAt = DateTime.UtcNow.AddDays(-5),
                ExpiresAt = DateTime.UtcNow.AddDays(2),
                ActivityId = Guid.NewGuid()
            },
            new PendingPasswordChange
            {
                MetaverseObjectId = mvoB,
                ConnectedSystemId = systemId,
                ConnectedSystemObjectId = csoId,
                Origin = PendingPasswordChangeOrigin.Provisioned,
                SyncRuleId = ruleId,
                Status = PendingPasswordChangeStatus.Parked,
                FailureReason = PasswordSetFailureReason.PolicyRejection,
                TargetMessage = "Password too short",
                LastAttemptedAt = laterTooShort,
                CreatedAt = DateTime.UtcNow.AddDays(-5),
                ExpiresAt = DateTime.UtcNow.AddDays(2),
                ActivityId = Guid.NewGuid()
            },
            new PendingPasswordChange
            {
                MetaverseObjectId = mvoC,
                ConnectedSystemId = systemId,
                ConnectedSystemObjectId = csoId,
                Origin = PendingPasswordChangeOrigin.Provisioned,
                SyncRuleId = ruleId,
                Status = PendingPasswordChangeStatus.Parked,
                FailureReason = PasswordSetFailureReason.UnsupportedOperation,
                TargetMessage = "Policy violation",
                LastAttemptedAt = policyViolationSeenAt,
                CreatedAt = DateTime.UtcNow.AddDays(-5),
                ExpiresAt = DateTime.UtcNow.AddDays(2),
                ActivityId = Guid.NewGuid()
            }
        };

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.QueuePasswordChangesAsync(changes);

        await using var ctx = NewContext();
        var reasons = await new PostgresDataRepository(ctx).Sync.GetParkedProvisionedPasswordReasonsAsync(ruleId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reasons, Has.Count.EqualTo(2));
            Assert.That(reasons[0].TargetMessage, Is.EqualTo("Password too short"));
            Assert.That(reasons[0].AccountCount, Is.EqualTo(2));
            Assert.That(reasons[0].FirstSeenAt, Is.EqualTo(earliestTooShort));
            Assert.That(reasons[1].TargetMessage, Is.EqualTo("Policy violation"));
            Assert.That(reasons[1].AccountCount, Is.EqualTo(1));
        }
    }

    #endregion

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
