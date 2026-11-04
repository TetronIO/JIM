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
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the Exchange queue's staging write (#2021).
/// <para>
/// Everything here is invisible to the in-memory provider by construction. Staging is an
/// <c>INSERT ... ON CONFLICT DO UPDATE</c> against a filtered unique index the in-memory provider does not enforce, the
/// leave-unchanged decision is made in SQL, and the row is written by raw SQL that bypasses the EF model, so a column
/// missed by the writer, or written in the wrong position or with the wrong type, persists wrongly with no error.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent. <c>SetUp</c> TRUNCATEs every table.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ExchangeChangeQueueDatabaseTests
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

    /// <summary>
    /// Every field survives the raw-SQL write path. The completeness test proves the column list matches the model;
    /// only a round trip proves the writer puts the right value, of the right type, in each column.
    /// </summary>
    [Test]
    public async Task StageExchangeChangesAsync_RoundTripsEveryFieldAsync()
    {
        var seeded = await SeedAsync();
        var createdAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        var change = new PendingExchangeChange
        {
            ConnectedSystemId = seeded.SystemId,
            ConnectedSystemObjectId = seeded.CsoId,
            MetaverseObjectId = seeded.MvoId,
            SyncRuleId = seeded.SyncRuleId,
            RecipientType = "MailUser",
            Alias = "ada.lovelace",
            Database = "DB01",
            PrimarySmtpAddress = "ada.lovelace@example.com",
            ExternalAddress = "SMTP:ada@partner.example",
            DomainController = "dc01.corp.example",
            Status = PendingExchangeChangeStatus.Parked,
            FailureKind = ExchangeChangeFailureKind.InsufficientPermissions,
            FailureMessage = "Access denied.",
            AttemptCount = 3,
            CreatedAt = createdAt,
            LastAttemptedAt = createdAt.AddMinutes(1),
            FirstFailedAt = createdAt.AddMinutes(2),
            NextRetryAt = createdAt.AddMinutes(3),
            ClaimedAt = createdAt.AddMinutes(4),
            ClaimedBy = "worker-1a2b3c4d",
            ActivityId = Guid.NewGuid()
        };

        var outcome = await StageAsync(change);

        await using var verify = NewContext();
        var stored = await verify.PendingExchangeChanges.AsNoTracking().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Inserted));
            Assert.That(outcome.RequestedId, Is.EqualTo(change.Id));
            Assert.That(outcome.RowId, Is.EqualTo(change.Id));
            Assert.That(outcome.PreviousActivityId, Is.Null);
            Assert.That(stored.Id, Is.EqualTo(change.Id));
            Assert.That(stored.ConnectedSystemId, Is.EqualTo(seeded.SystemId));
            Assert.That(stored.ConnectedSystemObjectId, Is.EqualTo(seeded.CsoId));
            Assert.That(stored.MetaverseObjectId, Is.EqualTo(seeded.MvoId));
            Assert.That(stored.SyncRuleId, Is.EqualTo(seeded.SyncRuleId));
            Assert.That(stored.RecipientType, Is.EqualTo("MailUser"));
            Assert.That(stored.Alias, Is.EqualTo("ada.lovelace"));
            Assert.That(stored.Database, Is.EqualTo("DB01"));
            Assert.That(stored.PrimarySmtpAddress, Is.EqualTo("ada.lovelace@example.com"));
            Assert.That(stored.ExternalAddress, Is.EqualTo("SMTP:ada@partner.example"));
            Assert.That(stored.DomainController, Is.EqualTo("dc01.corp.example"));
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Parked));
            Assert.That(stored.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.InsufficientPermissions));
            Assert.That(stored.FailureMessage, Is.EqualTo("Access denied."));
            Assert.That(stored.AttemptCount, Is.EqualTo(3));
            Assert.That(stored.CreatedAt, Is.EqualTo(createdAt));
            Assert.That(stored.LastAttemptedAt, Is.EqualTo(change.LastAttemptedAt));
            Assert.That(stored.FirstFailedAt, Is.EqualTo(change.FirstFailedAt));
            Assert.That(stored.NextRetryAt, Is.EqualTo(change.NextRetryAt));
            Assert.That(stored.ClaimedAt, Is.EqualTo(change.ClaimedAt));
            Assert.That(stored.ClaimedBy, Is.EqualTo("worker-1a2b3c4d"));
            Assert.That(stored.ActivityId, Is.EqualTo(change.ActivityId));
        }
    }

    /// <summary>
    /// A newer declared state for an account already on the queue supersedes its row in place: one row per account,
    /// keeping its id, carrying the newer state, and reset to be due at once with every trace of the earlier attempts
    /// gone. The superseded row's Activity is handed back so the supersession can be recorded under it.
    /// </summary>
    [Test]
    public async Task StageExchangeChangesAsync_ParkedRowForTheAccount_SupersedesInPlaceAsync()
    {
        var seeded = await SeedAsync();
        var existing = await StageExistingAsync(seeded, row =>
        {
            row.Status = PendingExchangeChangeStatus.Parked;
            row.FailureKind = ExchangeChangeFailureKind.Refused;
            row.FailureMessage = "The alias is already in use.";
            row.AttemptCount = 4;
            row.LastAttemptedAt = DateTime.UtcNow.AddMinutes(-5);
            row.FirstFailedAt = DateTime.UtcNow.AddHours(-25);
            row.NextRetryAt = DateTime.UtcNow.AddMinutes(10);
        });
        var otherMvoId = await SeedIdentityAsync();
        var newerCreatedAt = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var newer = NewChange(seeded, recipientType: "SharedMailbox", alias: "reception");
        newer.MetaverseObjectId = otherMvoId;
        newer.SyncRuleId = null;
        newer.DomainController = "dc02.corp.example";
        newer.ExternalAddress = "SMTP:reception@partner.example";
        newer.CreatedAt = newerCreatedAt;

        var outcome = await StageAsync(newer);

        await using var verify = NewContext();
        var stored = await verify.PendingExchangeChanges.AsNoTracking().SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Superseded));
            Assert.That(outcome.RowId, Is.EqualTo(existing.Id));
            Assert.That(outcome.RequestedId, Is.Not.EqualTo(existing.Id));
            Assert.That(outcome.PreviousActivityId, Is.EqualTo(existing.ActivityId));
            Assert.That(newer.Id, Is.EqualTo(existing.Id), "the caller holds the id actually in the queue");
            Assert.That(stored.Id, Is.EqualTo(existing.Id));
            Assert.That(stored.ConnectedSystemObjectId, Is.EqualTo(seeded.CsoId));
            Assert.That(stored.MetaverseObjectId, Is.EqualTo(otherMvoId));
            Assert.That(stored.SyncRuleId, Is.Null);
            Assert.That(stored.RecipientType, Is.EqualTo("SharedMailbox"));
            Assert.That(stored.Alias, Is.EqualTo("reception"));
            Assert.That(stored.Database, Is.EqualTo("DB01"));
            Assert.That(stored.ExternalAddress, Is.EqualTo("SMTP:reception@partner.example"));
            Assert.That(stored.DomainController, Is.EqualTo("dc02.corp.example"));
            Assert.That(stored.ActivityId, Is.EqualTo(newer.ActivityId));
            Assert.That(stored.CreatedAt, Is.EqualTo(newerCreatedAt));
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.FailureKind, Is.Null);
            Assert.That(stored.FailureMessage, Is.Null);
            Assert.That(stored.AttemptCount, Is.Zero);
            Assert.That(stored.LastAttemptedAt, Is.Null);
            Assert.That(stored.FirstFailedAt, Is.Null);
            Assert.That(stored.NextRetryAt, Is.Null);
        }
    }

    /// <summary>
    /// A row being delivered is superseded too, and its claim cleared: that is what makes the in-flight attempt's
    /// outcome write, guarded on the row still being Delivering, land nowhere.
    /// </summary>
    [Test]
    public async Task StageExchangeChangesAsync_DeliveringRowForTheAccount_SupersedesItAndClearsTheClaimAsync()
    {
        var seeded = await SeedAsync();
        var existing = await StageExistingAsync(seeded, row =>
        {
            row.Status = PendingExchangeChangeStatus.Delivering;
            row.ClaimedAt = DateTime.UtcNow;
            row.ClaimedBy = "worker-1a2b3c4d";
            row.AttemptCount = 1;
        });

        var outcome = await StageAsync(NewChange(seeded, alias: "ada"));

        await using var verify = NewContext();
        var stored = await verify.PendingExchangeChanges.AsNoTracking().SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Superseded));
            Assert.That(outcome.RowId, Is.EqualTo(existing.Id));
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.ClaimedAt, Is.Null);
            Assert.That(stored.ClaimedBy, Is.Null);
            Assert.That(stored.AttemptCount, Is.Zero);
            Assert.That(stored.Alias, Is.EqualTo("ada"));
        }
    }

    /// <summary>
    /// Re-staging exactly what a held row declares, as an export re-evaluated as unconfirmed does, leaves it untouched.
    /// Null and empty declare the same thing, in every one of the five values.
    /// </summary>
    [Test]
    public async Task StageExchangeChangesAsync_SameDeclaredStateNullAndEmptyAlike_LeavesItUnchangedAsync()
    {
        var seeded = await SeedAsync();
        var existing = await StageExistingAsync(seeded, row =>
        {
            row.Alias = null;
            row.Database = "";
            row.PrimarySmtpAddress = null;
            row.ExternalAddress = "";
            row.Status = PendingExchangeChangeStatus.Parked;
            row.FailureKind = ExchangeChangeFailureKind.Refused;
        });
        var restaged = NewChange(seeded, alias: "");
        restaged.Database = null;
        restaged.PrimarySmtpAddress = "";
        restaged.ExternalAddress = null;
        restaged.DomainController = "dc02.corp.example";

        var outcome = await StageAsync(restaged);

        await using var verify = NewContext();
        var stored = await verify.PendingExchangeChanges.AsNoTracking().SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Unchanged));
            Assert.That(outcome.ExistingStatus, Is.EqualTo(PendingExchangeChangeStatus.Parked));
            Assert.That(outcome.RowId, Is.EqualTo(existing.Id));
            Assert.That(outcome.RequestedId, Is.Not.EqualTo(existing.Id));
            Assert.That(outcome.PreviousActivityId, Is.Null);
            Assert.That(restaged.Id, Is.EqualTo(existing.Id));
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Parked));
            Assert.That(stored.ActivityId, Is.EqualTo(existing.ActivityId), "a held row keeps its own Activity");
            Assert.That(stored.DomainController, Is.EqualTo("dc01.corp.example"), "a held row is left untouched");
            Assert.That(stored.Database, Is.EqualTo(""));
        }
    }

    /// <summary>
    /// Re-staging the declared state a Parked row already carries leaves it parked, untouched: a parked change holds
    /// until a person retries it or the declared state changes (PRD requirement 11).
    /// </summary>
    [Test]
    public async Task StageExchangeChangesAsync_ParkedRowWithTheSameDeclaredState_LeavesItUnchangedAsync()
    {
        var seeded = await SeedAsync();
        var firstFailedAt = new DateTime(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);
        var existing = await StageExistingAsync(seeded, row =>
        {
            row.Status = PendingExchangeChangeStatus.Parked;
            row.FailureKind = ExchangeChangeFailureKind.Refused;
            row.FailureMessage = "The alias is already in use.";
            row.AttemptCount = 4;
            row.FirstFailedAt = firstFailedAt;
        });
        var restaged = NewChange(seeded);
        restaged.DomainController = "dc02.corp.example";

        var outcome = await StageAsync(restaged);

        await using var verify = NewContext();
        var stored = await verify.PendingExchangeChanges.AsNoTracking().SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Unchanged));
            Assert.That(outcome.ExistingStatus, Is.EqualTo(PendingExchangeChangeStatus.Parked));
            Assert.That(outcome.RowId, Is.EqualTo(existing.Id));
            Assert.That(outcome.PreviousActivityId, Is.Null);
            Assert.That(restaged.Id, Is.EqualTo(existing.Id));
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Parked));
            Assert.That(stored.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Refused));
            Assert.That(stored.FailureMessage, Is.EqualTo("The alias is already in use."));
            Assert.That(stored.AttemptCount, Is.EqualTo(4));
            Assert.That(stored.FirstFailedAt, Is.EqualTo(firstFailedAt));
            Assert.That(stored.ActivityId, Is.EqualTo(existing.ActivityId));
            Assert.That(stored.DomainController, Is.EqualTo("dc01.corp.example"), "an unchanged row is left untouched");
        }
    }

    /// <summary>
    /// Re-staging the declared state a retrying row already carries keeps its attempts and its backoff.
    /// </summary>
    [Test]
    public async Task StageExchangeChangesAsync_PendingRowWithAttemptsAndTheSameDeclaredState_KeepsItsBackoffAsync()
    {
        var seeded = await SeedAsync();
        var nextRetryAt = new DateTime(2026, 11, 1, 12, 8, 0, DateTimeKind.Utc);
        await StageExistingAsync(seeded, row =>
        {
            row.FailureKind = ExchangeChangeFailureKind.Unreachable;
            row.AttemptCount = 2;
            row.NextRetryAt = nextRetryAt;
        });

        var outcome = await StageAsync(NewChange(seeded));

        await using var verify = NewContext();
        var stored = await verify.PendingExchangeChanges.AsNoTracking().SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Unchanged));
            Assert.That(outcome.ExistingStatus, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.AttemptCount, Is.EqualTo(2));
            Assert.That(stored.NextRetryAt, Is.EqualTo(nextRetryAt));
            Assert.That(stored.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Unreachable));
        }
    }

    /// <summary>
    /// A supersede writes exactly the offered row's status and failure fields (a refusal records itself and its reason),
    /// while attempts and claim are still cleared.
    /// </summary>
    [Test]
    public async Task StageExchangeChangesAsync_RefusalSupersedingAQueuedChange_WritesTheRefusalAndClearsTheAttemptsAsync()
    {
        var seeded = await SeedAsync();
        await StageExistingAsync(seeded, row =>
        {
            row.Status = PendingExchangeChangeStatus.Delivering;
            row.FailureKind = ExchangeChangeFailureKind.Unreachable;
            row.AttemptCount = 2;
            row.LastAttemptedAt = DateTime.UtcNow;
            row.FirstFailedAt = DateTime.UtcNow.AddHours(-1);
            row.NextRetryAt = DateTime.UtcNow.AddMinutes(4);
            row.ClaimedAt = DateTime.UtcNow;
            row.ClaimedBy = "worker-1a2b3c4d";
        });
        var refusal = NewChange(seeded, recipientType: "SharedMailbox");
        refusal.Status = PendingExchangeChangeStatus.Refused;
        refusal.FailureMessage = "Changing a recipient's type is not supported in this release.";

        var outcome = await StageAsync(refusal);

        await using var verify = NewContext();
        var stored = await verify.PendingExchangeChanges.AsNoTracking().SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Superseded));
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Refused));
            Assert.That(stored.FailureMessage, Is.EqualTo("Changing a recipient's type is not supported in this release."));
            Assert.That(stored.FailureKind, Is.Null);
            Assert.That(stored.AttemptCount, Is.Zero);
            Assert.That(stored.LastAttemptedAt, Is.Null);
            Assert.That(stored.FirstFailedAt, Is.Null);
            Assert.That(stored.NextRetryAt, Is.Null);
            Assert.That(stored.ClaimedAt, Is.Null);
            Assert.That(stored.ClaimedBy, Is.Null);
        }
    }

    /// <summary>
    /// Whether a change is refused is part of what it is: the same declared state refused before and deliverable now,
    /// or the reverse, supersedes the row; refused again, it is left as it was.
    /// </summary>
    [TestCase(PendingExchangeChangeStatus.Refused, PendingExchangeChangeStatus.Pending, ExchangeChangeStagingDisposition.Superseded)]
    [TestCase(PendingExchangeChangeStatus.Pending, PendingExchangeChangeStatus.Refused, ExchangeChangeStagingDisposition.Superseded)]
    [TestCase(PendingExchangeChangeStatus.Refused, PendingExchangeChangeStatus.Refused, ExchangeChangeStagingDisposition.Unchanged)]
    public async Task StageExchangeChangesAsync_SameDeclaredStateRefusedOrNot_SupersedesOnlyWhenTheRefusalChangedAsync(
        PendingExchangeChangeStatus existingStatus, PendingExchangeChangeStatus offeredStatus, ExchangeChangeStagingDisposition expected)
    {
        var seeded = await SeedAsync();
        await StageExistingAsync(seeded, row =>
        {
            row.Status = existingStatus;
            row.FailureMessage = existingStatus == PendingExchangeChangeStatus.Refused ? "first" : null;
        });
        var offered = NewChange(seeded);
        offered.Status = offeredStatus;
        offered.FailureMessage = offeredStatus == PendingExchangeChangeStatus.Refused ? "second" : null;

        var outcome = await StageAsync(offered);

        await using var verify = NewContext();
        var stored = await verify.PendingExchangeChanges.AsNoTracking().SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Disposition, Is.EqualTo(expected));
            Assert.That(stored.Status, Is.EqualTo(offeredStatus));
            Assert.That(stored.FailureMessage, Is.EqualTo(expected == ExchangeChangeStagingDisposition.Unchanged ? "first" : offered.FailureMessage));
        }
    }

    /// <summary>
    /// A different declared state replaces a parked row like any other, and the park goes with the state it was earned
    /// by: changing the declaration is how an administrator moves a change JIM has stopped on (PRD decision 11).
    /// </summary>
    [TestCase("RecipientType")]
    [TestCase("Alias")]
    [TestCase("Database")]
    [TestCase("PrimarySmtpAddress")]
    [TestCase("ExternalAddress")]
    public async Task StageExchangeChangesAsync_ParkedRowWithADifferentDeclaredState_SupersedesItAsync(string differing)
    {
        var seeded = await SeedAsync();
        var existing = await StageExistingAsync(seeded, row =>
        {
            row.Status = PendingExchangeChangeStatus.Parked;
            row.FailureKind = ExchangeChangeFailureKind.Refused;
            row.FailureMessage = "The alias is already in use.";
            row.AttemptCount = 4;
            row.LastAttemptedAt = DateTime.UtcNow;
            row.FirstFailedAt = DateTime.UtcNow.AddHours(-25);
        });
        var newer = NewChange(seeded);
        switch (differing)
        {
            case "RecipientType": newer.RecipientType = "SharedMailbox"; break;
            case "Alias": newer.Alias = "Ada.Lovelace"; break;
            case "Database": newer.Database = null; break;
            case "PrimarySmtpAddress": newer.PrimarySmtpAddress = "ada@example.com"; break;
            case "ExternalAddress": newer.ExternalAddress = "SMTP:ada@partner.example"; break;
        }

        var outcome = await StageAsync(newer);

        await using var verify = NewContext();
        var stored = await verify.PendingExchangeChanges.AsNoTracking().SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Disposition, Is.EqualTo(ExchangeChangeStagingDisposition.Superseded), $"{differing} differs");
            Assert.That(outcome.PreviousActivityId, Is.EqualTo(existing.ActivityId));
            Assert.That(stored.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.FailureKind, Is.Null);
            Assert.That(stored.FailureMessage, Is.Null);
            Assert.That(stored.AttemptCount, Is.Zero);
            Assert.That(stored.LastAttemptedAt, Is.Null);
            Assert.That(stored.FirstFailedAt, Is.Null, "the newer state does not inherit the 24-hour failure window");
            Assert.That(stored.ActivityId, Is.EqualTo(newer.ActivityId));
        }
    }

    /// <summary>
    /// The unique key holds within one call as well: two changes for one account coalesce onto one row, the later
    /// declared state winning; changes for different accounts each get their own.
    /// </summary>
    [Test]
    public async Task StageExchangeChangesAsync_TwoChangesForOneAccountInOneCall_CoalesceOntoOneRowAsync()
    {
        var seeded = await SeedAsync();
        var secondCsoId = await SeedConnectedSystemObjectAsync(seeded.SystemId);
        var first = NewChange(seeded, alias: "first");
        var second = NewChange(seeded, alias: "second");
        var otherAccount = NewChange(seeded, alias: "other");
        otherAccount.ConnectedSystemObjectId = secondCsoId;

        List<ExchangeChangeStagingOutcome> outcomes;
        await using (var write = NewContext())
            outcomes = await new PostgresDataRepository(write).Sync.StageExchangeChangesAsync([first, second, otherAccount]);

        await using var verify = NewContext();
        var stored = await verify.PendingExchangeChanges.AsNoTracking().ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes.Select(o => o.Disposition), Is.EqualTo(new[]
            {
                ExchangeChangeStagingDisposition.Inserted,
                ExchangeChangeStagingDisposition.Superseded,
                ExchangeChangeStagingDisposition.Inserted
            }));
            Assert.That(outcomes[1].RowId, Is.EqualTo(outcomes[0].RowId));
            Assert.That(stored, Has.Count.EqualTo(2));
            Assert.That(stored.Single(r => r.ConnectedSystemObjectId == seeded.CsoId).Alias, Is.EqualTo("second"));
            Assert.That(stored.Single(r => r.ConnectedSystemObjectId == secondCsoId).Alias, Is.EqualTo("other"));
        }
    }

    /// <summary>
    /// A change must name its account: the queue coalesces on it, and a change for no account is one nothing could
    /// deliver. Refused before anything is written.
    /// </summary>
    [Test]
    public async Task StageExchangeChangesAsync_ChangeNamingNoAccount_ThrowsAndWritesNothingAsync()
    {
        var seeded = await SeedAsync();
        var change = NewChange(seeded);
        change.ConnectedSystemObjectId = null;

        await using (var write = NewContext())
        {
            Assert.That(() => new PostgresDataRepository(write).Sync.StageExchangeChangesAsync([change]),
                Throws.ArgumentException);
        }

        await using var verify = NewContext();
        Assert.That(await verify.PendingExchangeChanges.CountAsync(), Is.Zero);
    }

    /// <summary>
    /// Deleting the account clears the reference and keeps the row, so the delivery lane can withdraw it under its own
    /// Activity rather than the change vanishing; deleting the identity and the rule likewise. Deleting the Connected
    /// System takes the row with it.
    /// </summary>
    [Test]
    public async Task DeletingTheAccountIdentityAndRule_ClearsTheReferencesAndKeepsTheRowAsync()
    {
        var seeded = await SeedAsync();
        await StageAsync(NewChange(seeded));

        await using (var delete = NewContext())
        {
            await delete.Database.ExecuteSqlRawAsync("""DELETE FROM "ConnectedSystemObjects" WHERE "Id" = {0}""", seeded.CsoId);
            await delete.Database.ExecuteSqlRawAsync("""DELETE FROM "MetaverseObjects" WHERE "Id" = {0}""", seeded.MvoId);
            await delete.Database.ExecuteSqlRawAsync("""DELETE FROM "SyncRules" WHERE "Id" = {0}""", seeded.SyncRuleId);
        }

        await using (var verify = NewContext())
        {
            var stored = await verify.PendingExchangeChanges.AsNoTracking().SingleAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stored.ConnectedSystemObjectId, Is.Null);
                Assert.That(stored.MetaverseObjectId, Is.Null);
                Assert.That(stored.SyncRuleId, Is.Null);
            }
        }

        await using (var delete = NewContext())
            await delete.Database.ExecuteSqlRawAsync("""DELETE FROM "ConnectedSystems" WHERE "Id" = {0}""", seeded.SystemId);

        await using var afterSystem = NewContext();
        Assert.That(await afterSystem.PendingExchangeChanges.CountAsync(), Is.Zero);
    }

    /// <summary>
    /// Staging wakes the Exchange delivery lane: the queue's trigger notifies on the channel the lane listens to, with
    /// the Connected System id as the payload.
    /// </summary>
    [Test]
    public async Task StageExchangeChangesAsync_RaisesExchangeChangeNotificationAsync()
    {
        var seeded = await SeedAsync();
        var received = new List<(string Channel, string Payload)>();
        await using var listener = new NpgsqlConnection(_connectionString + ";Pooling=false");
        listener.Notification += (_, args) => received.Add((args.Channel, args.Payload));
        await listener.OpenAsync();
        await using (var listen = new NpgsqlCommand($"LISTEN \"{Constants.NotificationChannels.ExchangeChange}\"", listener))
            await listen.ExecuteNonQueryAsync();

        await StageAsync(NewChange(seeded));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (received.Count == 0)
            await listener.WaitAsync(cts.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(received[0].Channel, Is.EqualTo(Constants.NotificationChannels.ExchangeChange));
            Assert.That(received[0].Payload, Is.EqualTo(seeded.SystemId.ToString()));
        }
    }

    #region Reading where a change stands (Phase 6)

    /// <summary>
    /// A follow-up reads its change's row by the change's Activity; rows carrying other changes are not returned.
    /// </summary>
    [Test]
    public async Task GetExchangeChangesByActivityAsync_ReturnsOnlyTheRowsCarryingThoseActivitiesAsync()
    {
        var seeded = await SeedAsync();
        var asked = NewChange(seeded, alias: "ada");
        var other = NewChange(seeded with { CsoId = await SeedConnectedSystemObjectAsync(seeded.SystemId) }, alias: "grace");
        await StageAsync(asked);
        await StageAsync(other);

        await using var read = NewContext();
        var rows = await new PostgresDataRepository(read).Sync.GetExchangeChangesByActivityAsync([asked.ActivityId, Guid.NewGuid()]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows.Single().Id, Is.EqualTo(asked.Id));
            Assert.That(rows.Single().Alias, Is.EqualTo("ada"));
            Assert.That(rows.Single().ConnectedSystemObjectId, Is.EqualTo(seeded.CsoId));
            Assert.That(await new PostgresDataRepository(read).Sync.GetExchangeChangesByActivityAsync([]), Is.Empty);
        }
    }

    [Test]
    public async Task GetExchangeChangeForConnectedSystemObjectAsync_ReturnsTheAccountsRowOrNullAsync()
    {
        var seeded = await SeedAsync();
        var change = NewChange(seeded);
        await StageAsync(change);
        var unqueued = await SeedConnectedSystemObjectAsync(seeded.SystemId);

        await using var read = NewContext();
        var repository = new PostgresDataRepository(read).Sync;

        using (Assert.EnterMultipleScope())
        {
            Assert.That((await repository.GetExchangeChangeForConnectedSystemObjectAsync(seeded.CsoId))?.Id, Is.EqualTo(change.Id));
            Assert.That(await repository.GetExchangeChangeForConnectedSystemObjectAsync(unqueued), Is.Null);
        }
    }

    /// <summary>
    /// Each staged change has an Activity of its own, and the database holds staging to it: two rows naming one Activity
    /// would leave a follow-up describing whichever it happened to read.
    /// </summary>
    [Test]
    public async Task StageExchangeChangesAsync_TwoChangesNamingOneActivity_IsRefusedByTheDatabaseAsync()
    {
        var seeded = await SeedAsync();
        var first = NewChange(seeded);
        await StageAsync(first);
        var second = NewChange(seeded with { CsoId = await SeedConnectedSystemObjectAsync(seeded.SystemId) });
        second.ActivityId = first.ActivityId;

        var thrown = Assert.CatchAsync(async () => await StageAsync(second));

        Assert.That(thrown is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
                    || thrown?.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation },
            Is.True, $"expected a unique violation, got {thrown?.GetType().Name}: {thrown?.Message}");
    }

    /// <summary>
    /// What became of each change: the Activities recorded under it, oldest first, projected with what the outcome read
    /// needs; those under other Activities are left out.
    /// </summary>
    [Test]
    public async Task GetExchangeChangeEventsAsync_ReturnsTheActivitiesUnderEachChangeOldestFirstAsync()
    {
        var changeActivityId = Guid.NewGuid();
        var otherActivityId = Guid.NewGuid();
        var at = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        await using (var seed = NewContext())
        {
            seed.Activities.AddRange(
                ChangeActivity(changeActivityId, null, at, ActivityTargetOperationType.SetExchangeRecipient, ActivityStatus.Complete),
                ChangeActivity(Guid.NewGuid(), changeActivityId, at.AddMinutes(6), ActivityTargetOperationType.WithdrawExchangeRecipient,
                    ActivityStatus.Complete, message: "Withdrawn: the account was removed."),
                ChangeActivity(Guid.NewGuid(), changeActivityId, at.AddMinutes(2), ActivityTargetOperationType.SetExchangeRecipient,
                    ActivityStatus.CompleteWithError, error: "Exchange did not answer."),
                ChangeActivity(Guid.NewGuid(), otherActivityId, at.AddMinutes(1), ActivityTargetOperationType.SetExchangeRecipient,
                    ActivityStatus.Complete));
            await seed.SaveChangesAsync();
        }

        await using var read = NewContext();
        var events = await new PostgresDataRepository(read).Activity.GetExchangeChangeEventsAsync([changeActivityId]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(events.Select(e => e.Operation), Is.EqualTo(new[]
            {
                ActivityTargetOperationType.SetExchangeRecipient, ActivityTargetOperationType.WithdrawExchangeRecipient
            }), "oldest first, and only those under the change asked about");
            Assert.That(events.All(e => e.ChangeActivityId == changeActivityId), Is.True);
            Assert.That(events[0].Status, Is.EqualTo(ActivityStatus.CompleteWithError));
            Assert.That(events[0].ErrorMessage, Is.EqualTo("Exchange did not answer."));
            Assert.That(events[0].OccurredAt, Is.EqualTo(at.AddMinutes(2)));
            Assert.That(events[1].Message, Is.EqualTo("Withdrawn: the account was removed."));
        }
    }

    private static Activity ChangeActivity(Guid id, Guid? parentId, DateTime created, ActivityTargetOperationType operation, ActivityStatus status,
        string? message = null, string? error = null) => new()
    {
        Id = id,
        ParentActivityId = parentId,
        TargetType = ActivityTargetType.ExchangeRecipient,
        TargetOperationType = operation,
        TargetName = "Corporate AD",
        Status = status,
        Message = message,
        ErrorMessage = error,
        InitiatedByType = ActivityInitiatorType.System,
        InitiatedByName = "System",
        Created = created,
        Executed = created
    };

    #endregion

    #region helpers

    private sealed record Seeded(int SystemId, Guid CsoId, Guid MvoId, int SyncRuleId);

    private static PendingExchangeChange NewChange(Seeded seeded, string recipientType = "Mailbox", string? alias = "ada.lovelace") => new()
    {
        ConnectedSystemId = seeded.SystemId,
        ConnectedSystemObjectId = seeded.CsoId,
        MetaverseObjectId = seeded.MvoId,
        SyncRuleId = seeded.SyncRuleId,
        RecipientType = recipientType,
        Alias = alias,
        Database = "DB01",
        DomainController = "dc01.corp.example",
        CreatedAt = DateTime.UtcNow,
        ActivityId = Guid.NewGuid()
    };

    private async Task<ExchangeChangeStagingOutcome> StageAsync(PendingExchangeChange change)
    {
        await using var write = NewContext();
        return (await new PostgresDataRepository(write).Sync.StageExchangeChangesAsync([change])).Single();
    }

    /// <summary>
    /// A row already on the queue for the seeded account, in the state an earlier attempt or an administrator left it:
    /// inserted through staging, then adjusted directly, as the delivery lane would leave it.
    /// </summary>
    private async Task<PendingExchangeChange> StageExistingAsync(Seeded seeded, Action<PendingExchangeChange> leftAs)
    {
        var change = NewChange(seeded);
        await StageAsync(change);

        await using var adjust = NewContext();
        var row = await adjust.PendingExchangeChanges.SingleAsync(r => r.Id == change.Id);
        leftAs(row);
        await adjust.SaveChangesAsync();
        return row;
    }

    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Corporate AD Connector", BuiltIn = true };
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
            ExternalIdAttributeId = externalIdAttribute.Id,
            MetaverseObjectId = mvo.Id
        };
        var syncRule = new SyncRule
        {
            Name = "Provision Users",
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id,
            MetaverseObjectTypeId = mvType.Id,
            ProvisionToConnectedSystem = true
        };
        seed.AddRange(cso, syncRule);
        await seed.SaveChangesAsync();

        return new Seeded(system.Id, cso.Id, mvo.Id, syncRule.Id);
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

    private async Task<Guid> SeedConnectedSystemObjectAsync(int systemId)
    {
        await using var seed = NewContext();
        var csType = await seed.ConnectedSystemObjectTypes.FirstAsync(t => t.ConnectedSystemId == systemId);
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

    #endregion
}
