// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of <c>SyncRepository.ApplyExportedAttributeValuesAsync</c> (issue
/// #1079: optimistic export apply). The in-memory unit suite (<c>OptimisticExportApplyCalculatorTests</c>,
/// <c>ExportExecutionTests</c>) proves the calculation and wiring; only a real database run can prove
/// the raw-SQL delete is safe against a context holding tracked instances of the affected rows, and
/// that the persisted round-trip is byte-for-byte what the calculator expects on a subsequent pass.
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other
/// <c>RequiresPostgres</c> fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is absent. Do NOT run
/// this fixture outside the sanctioned scratch-database workflow: <c>SetUp</c> TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class OptimisticExportApplyDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL optimistic export apply tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    /// <summary>
    /// Seeds a Connected System with one object type, one Text attribute, and one Normal-status
    /// CSO carrying the given initial attribute values.
    /// </summary>
    private async Task<(ConnectedSystemObjectTypeAttribute Attribute, Guid CsoId)> SeedCsoAsync(
        params string[] initialValues)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone HR", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var mailAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        csType.Attributes.Add(mailAttr);
        seed.AddRange(connectorDefinition, system, csType, mailAttr);
        await seed.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = mailAttr.Id,
            LastUpdated = null
        };
        seed.Add(cso);
        await seed.SaveChangesAsync();

        seed.ConnectedSystemObjectAttributeValues.AddRange(initialValues.Select(stringValue => new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            AttributeId = mailAttr.Id,
            StringValue = stringValue
        }));
        await seed.SaveChangesAsync();

        return (mailAttr, cso.Id);
    }

    [Test]
    public async Task ApplyExportedAttributeValuesAsync_AllValueColumns_RoundTripAsync()
    {
        var (attr, csoId) = await SeedCsoAsync();
        await using var ctx = NewContext();
        var cso = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == csoId);
        var values = Enum.GetValues<AttributeDataType>().Where(t => t != AttributeDataType.NotSet)
            .Select(t => new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = attr.Id,
                StringValue = t == AttributeDataType.Text ? "text" : null,
                IntValue = t == AttributeDataType.Number ? 42 : null,
                LongValue = t == AttributeDataType.LongNumber ? 1234567890123L : null,
                DecimalValue = t == AttributeDataType.Decimal ? 123.456m : null,
                DateTimeValue = t == AttributeDataType.DateTime ? new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) : null,
                ByteValue = t == AttributeDataType.Binary ? new byte[] { 0, 127, 255 } : null,
                BoolValue = t == AttributeDataType.Boolean ? false : null,
                GuidValue = t == AttributeDataType.Guid ? Guid.NewGuid() : null,
                ReferenceValueId = t == AttributeDataType.Reference ? csoId : null,
                UnresolvedReferenceValue = t == AttributeDataType.Reference ? "CN=Reference,DC=test" : null
            }).ToList();
        await new PostgresDataRepository(ctx).Sync.ApplyExportedAttributeValuesAsync(values, [], [csoId]);
        await using var verify = NewContext();
        var stored = await verify.ConnectedSystemObjectAttributeValues.AsNoTracking()
            .Include(v => v.ConnectedSystemObject).ToDictionaryAsync(v => v.Id);
        Assert.That(stored, Has.Count.EqualTo(values.Count));
        foreach (var expected in values)
        {
            var actual = stored[expected.Id];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual.ConnectedSystemObject.Id, Is.EqualTo(csoId));
                Assert.That(actual.AttributeId, Is.EqualTo(expected.AttributeId));
                Assert.That(actual.StringValue, Is.EqualTo(expected.StringValue));
                Assert.That(actual.IntValue, Is.EqualTo(expected.IntValue));
                Assert.That(actual.LongValue, Is.EqualTo(expected.LongValue));
                Assert.That(actual.DecimalValue, Is.EqualTo(expected.DecimalValue));
                Assert.That(actual.DateTimeValue, Is.EqualTo(expected.DateTimeValue));
                Assert.That(actual.ByteValue, Is.EqualTo(expected.ByteValue));
                Assert.That(actual.BoolValue, Is.EqualTo(expected.BoolValue));
                Assert.That(actual.GuidValue, Is.EqualTo(expected.GuidValue));
                Assert.That(actual.ReferenceValueId, Is.EqualTo(expected.ReferenceValueId));
                Assert.That(actual.UnresolvedReferenceValue, Is.EqualTo(expected.UnresolvedReferenceValue));
            }
        }
    }

    /// <summary>
    /// (1) Apply inserts and deletes real rows, and leaves the parent CSO's LastUpdated and Status
    /// untouched (D2: re-arms the Full Synchronisation unchanged-object watermark).
    /// </summary>
    [Test]
    public async Task ApplyExportedAttributeValuesAsync_InsertsAndDeletesRealRows_LeavesLastUpdatedAndStatusUntouchedAsync()
    {
        var (attr, csoId) = await SeedCsoAsync("old@example.com");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var existing = await ctx.ConnectedSystemObjectAttributeValues.AsNoTracking()
            .SingleAsync(av => av.ConnectedSystemObject.Id == csoId);
        var cso = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == csoId);

        var newValue = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            AttributeId = attr.Id,
            StringValue = "new@example.com"
        };

        await repository.Sync.ApplyExportedAttributeValuesAsync([newValue], [existing.Id], [csoId]);

        await using var verify = NewContext();
        var storedValues = await verify.ConnectedSystemObjectAttributeValues
            .Where(av => av.ConnectedSystemObject.Id == csoId)
            .ToListAsync();
        Assert.That(storedValues.Select(v => v.StringValue), Is.EquivalentTo(new[] { "new@example.com" }),
            "the old row must be deleted and the new row inserted");

        var storedCso = await verify.ConnectedSystemObjects.SingleAsync(c => c.Id == csoId);
        Assert.That(storedCso.LastUpdated, Is.Null, "optimistic apply must never stamp LastUpdated (D2)");
        Assert.That(storedCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), "optimistic apply must never touch parent CSO fields (D2)");
    }

    /// <summary>
    /// (2) The raw-SQL delete is safe while a context holds tracked instances of the affected rows
    /// (the detach guard proof): a tracked attribute value that optimistic apply just deleted must
    /// not resurface as a DbUpdateConcurrencyException when the same context later cascade-deletes
    /// its parent CSO.
    /// </summary>
    [Test]
    public async Task ApplyExportedAttributeValuesAsync_DeletePathWhileContextHoldsTrackedInstance_DeletesWithoutConcurrencyConflictAsync()
    {
        var (_, csoId) = await SeedCsoAsync("doomed@example.com");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Track the attribute value AND its parent CSO on this context before the raw delete runs,
        // mirroring how the worker's long-lived context can hold instances loaded earlier in a run.
        var trackedCso = await ctx.ConnectedSystemObjects
            .Include(c => c.AttributeValues)
            .SingleAsync(c => c.Id == csoId);
        var trackedValue = trackedCso.AttributeValues.Single();

        await repository.Sync.ApplyExportedAttributeValuesAsync([], [trackedValue.Id], [csoId]);

        Assert.That(ctx.ChangeTracker.Entries<ConnectedSystemObjectAttributeValue>().Any(e => e.Entity.Id == trackedValue.Id), Is.False,
            "the raw delete must detach the tracked instance");

        // Deleting the parent CSO on this same context cascades to every STILL-tracked child; if the
        // attribute value above were left tracked (Unchanged), EF's cascade would issue a DELETE
        // against the already-gone row, affect 0 rows, and throw DbUpdateConcurrencyException.
        ctx.ConnectedSystemObjects.Remove(trackedCso);
        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.Nothing);

        await using var verify = NewContext();
        Assert.That(await verify.ConnectedSystemObjects.AnyAsync(c => c.Id == csoId), Is.False);
    }

    /// <summary>
    /// (2b) The set-based detach (#2813) is exact: with every value of the object tracked, only the removed ones leave
    /// the tracker, the rest stay tracked as they were, and a later save on the same context (deleting the object, which
    /// cascades to its still-tracked values) succeeds with the database holding nothing of the object afterwards.
    /// </summary>
    [Test]
    public async Task ApplyExportedAttributeValuesAsync_SomeOfManyTrackedValuesRemoved_DetachesExactlyThoseAndSaveSucceedsAsync()
    {
        var (_, csoId) = await SeedCsoAsync("kept1@example.com", "doomed1@example.com", "kept2@example.com", "doomed2@example.com");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var trackedCso = await ctx.ConnectedSystemObjects
            .Include(c => c.AttributeValues)
            .SingleAsync(c => c.Id == csoId);
        var removedIds = trackedCso.AttributeValues.Where(av => av.StringValue!.StartsWith("doomed", StringComparison.Ordinal)).Select(av => av.Id).ToList();
        var keptIds = trackedCso.AttributeValues.Where(av => av.StringValue!.StartsWith("kept", StringComparison.Ordinal)).Select(av => av.Id).ToList();

        await repository.Sync.ApplyExportedAttributeValuesAsync([], removedIds, [csoId]);

        var trackedValueIds = ctx.ChangeTracker.Entries<ConnectedSystemObjectAttributeValue>()
            .Where(e => e.State == EntityState.Unchanged)
            .Select(e => e.Entity.Id)
            .ToList();
        Assert.That(trackedValueIds, Is.EquivalentTo(keptIds),
            "exactly the removed values are detached; the kept ones stay tracked, unchanged");

        ctx.ConnectedSystemObjects.Remove(trackedCso);
        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.Nothing);

        await using var verify = NewContext();
        Assert.That(await verify.ConnectedSystemObjectAttributeValues.AnyAsync(av => EF.Property<Guid>(av, "ConnectedSystemObjectId") == csoId),
            Is.False, "the removed values were deleted by the apply and the kept ones by the cascade");
    }

    /// <summary>
    /// (3) Idempotency at the SQL layer: re-running the calculator against a Connected System
    /// Object freshly reloaded from the database after a prior apply finds the exported value
    /// already there and produces an empty delta, so a repeated ApplyExportedAttributeValuesAsync
    /// call for the same Pending Export is never issued with non-empty content.
    /// </summary>
    [Test]
    public async Task ApplyExportedAttributeValuesAsync_ReappliedAfterFreshReload_CalculatorFindsNothingLeftToDoAsync()
    {
        var (attr, csoId) = await SeedCsoAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var cso = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == csoId);
        var addedValue = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            AttributeId = attr.Id,
            StringValue = "idempotent@example.com"
        };

        await repository.Sync.ApplyExportedAttributeValuesAsync([addedValue], [], [csoId]);

        // Reload fresh from the database, as the next batch/run would, and re-run the calculator
        // against the SAME Pending Export attribute change.
        await using var verify = NewContext();
        var reloadedCso = await verify.ConnectedSystemObjects
            .Include(c => c.AttributeValues)
                .ThenInclude(av => av.Attribute)
            .SingleAsync(c => c.Id == csoId);

        var change = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = attr.Id,
            Attribute = attr,
            ChangeType = PendingExportAttributeChangeType.Add,
            StringValue = "idempotent@example.com"
        };
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = reloadedCso,
            ConnectedSystemObjectId = reloadedCso.Id,
            AttributeValueChanges = [change]
        };

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pendingExport]);

        Assert.That(delta.Additions, Is.Empty, "the persisted round-trip must satisfy the calculator's existence check");
        Assert.That(delta.RemovalValueIds, Is.Empty);
    }

    /// <summary>
    /// SPEC-1082 D9, test plan item 8: a CSO stamped with an import content hash whose values are
    /// then mutated by optimistic export apply must have both stamp columns nulled by the SAME
    /// call, so a subsequent Full Import never trusts a hash describing content that has since
    /// changed. LastUpdated must remain untouched (D2/#891), exactly as before this feature.
    /// </summary>
    [Test]
    public async Task ApplyExportedAttributeValuesAsync_StampedCso_NullsImportStateHashAndFingerprintAsync()
    {
        var (attr, csoId) = await SeedCsoAsync("old@example.com");

        // Simulate a prior Full Import stamp.
        await using (var stampCtx = NewContext())
        {
            var stampRepository = new PostgresDataRepository(stampCtx);
            await stampRepository.Sync.StampImportStateAsync([(csoId, Guid.NewGuid(), Guid.NewGuid())]);
        }

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var existing = await ctx.ConnectedSystemObjectAttributeValues.AsNoTracking()
            .SingleAsync(av => av.ConnectedSystemObject.Id == csoId);
        var cso = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == csoId);

        var newValue = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            AttributeId = attr.Id,
            StringValue = "new@example.com"
        };

        await repository.Sync.ApplyExportedAttributeValuesAsync([newValue], [existing.Id], [csoId]);

        await using var verify = NewContext();
        var storedCso = await verify.ConnectedSystemObjects.SingleAsync(c => c.Id == csoId);
        Assert.That(storedCso.ImportStateHash, Is.Null, "optimistic apply must null the stored import content hash (D9)");
        Assert.That(storedCso.ImportStateFingerprint, Is.Null, "optimistic apply must null the stored schema fingerprint (D9)");
        Assert.That(storedCso.LastUpdated, Is.Null, "optimistic apply must never stamp LastUpdated (D2)");
    }

    /// <summary>
    /// #2894: the delete, the COPY and the import-state update are one transaction. A COPY that fails after the delete
    /// has run (here an addition naming an attribute that does not exist, which the foreign key refuses) must leave the
    /// object exactly as it was: the superseded value still present, nothing new written, the import stamp intact, and
    /// the tracked instance of the value it meant to remove still tracked, since its row is still there.
    /// </summary>
    [Test]
    public async Task ApplyExportedAttributeValuesAsync_CopyFailsAfterDelete_LeavesSupersededValuesAndWritesNothingAsync()
    {
        var (attr, csoId) = await SeedCsoAsync("old@example.com");
        var stamp = (Hash: Guid.NewGuid(), Fingerprint: Guid.NewGuid());
        await using (var stampCtx = NewContext())
            await new PostgresDataRepository(stampCtx).Sync.StampImportStateAsync([(csoId, stamp.Hash, stamp.Fingerprint)]);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var trackedCso = await ctx.ConnectedSystemObjects.Include(c => c.AttributeValues).SingleAsync(c => c.Id == csoId);
        var existing = trackedCso.AttributeValues.Single();

        var validAddition = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = trackedCso, AttributeId = attr.Id, StringValue = "new@example.com"
        };
        var invalidAddition = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = trackedCso, AttributeId = attr.Id + 999_999, StringValue = "broken@example.com"
        };

        Assert.That(async () => await repository.Sync.ApplyExportedAttributeValuesAsync(
                [validAddition, invalidAddition], [existing.Id], [csoId]),
            Throws.InstanceOf<Npgsql.PostgresException>().With.Property(nameof(Npgsql.PostgresException.SqlState)).EqualTo("23503"));

        await AssertObjectUnchangedAsync(csoId, existing.Id, "old@example.com", stamp);
        Assert.That(ctx.Entry(existing).State, Is.EqualTo(EntityState.Unchanged),
            "the value's row survived the failed apply, so its tracked instance must stay tracked as it was");
        Assert.That(ctx.Database.CurrentTransaction, Is.Null, "the apply's own transaction must not outlive the call");
    }

    /// <summary>
    /// #2894: as above, but with the failure in the last statement, the import-state update, after both the delete and the
    /// COPY have run. A trigger on the object table refuses the update for the length of the test.
    /// </summary>
    [Test]
    public async Task ApplyExportedAttributeValuesAsync_ImportStateUpdateFailsAfterDeleteAndCopy_LeavesObjectAsItWasAsync()
    {
        var (attr, csoId) = await SeedCsoAsync("old@example.com");
        var stamp = (Hash: Guid.NewGuid(), Fingerprint: Guid.NewGuid());
        await using (var stampCtx = NewContext())
            await new PostgresDataRepository(stampCtx).Sync.StampImportStateAsync([(csoId, stamp.Hash, stamp.Fingerprint)]);

        await using (var triggerCtx = NewContext())
            await triggerCtx.Database.ExecuteSqlRawAsync("""
                CREATE OR REPLACE FUNCTION test_2894_refuse_update() RETURNS trigger LANGUAGE plpgsql AS
                $$ BEGIN RAISE EXCEPTION 'refused by test 2894' USING ERRCODE = 'P2894'; END $$;
                CREATE TRIGGER test_2894_refuse_update BEFORE UPDATE ON "ConnectedSystemObjects"
                    FOR EACH ROW EXECUTE FUNCTION test_2894_refuse_update();
                """);
        try
        {
            await using var ctx = NewContext();
            var repository = new PostgresDataRepository(ctx);
            var existing = await ctx.ConnectedSystemObjectAttributeValues.AsNoTracking()
                .SingleAsync(av => av.ConnectedSystemObject.Id == csoId);
            var cso = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == csoId);
            var addition = new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = attr.Id, StringValue = "new@example.com"
            };

            Assert.That(async () => await repository.Sync.ApplyExportedAttributeValuesAsync([addition], [existing.Id], [csoId]),
                Throws.InstanceOf<Npgsql.PostgresException>().With.Property(nameof(Npgsql.PostgresException.SqlState)).EqualTo("P2894"));
        }
        finally
        {
            await using var dropCtx = NewContext();
            await dropCtx.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER IF EXISTS test_2894_refuse_update ON "ConnectedSystemObjects";
                DROP FUNCTION IF EXISTS test_2894_refuse_update();
                """);
        }

        var existingId = await SingleValueIdAsync(csoId);
        await AssertObjectUnchangedAsync(csoId, existingId, "old@example.com", stamp);
    }

    /// <summary>
    /// #2894: inside a transaction the caller already holds, the apply enlists in it rather than committing on its own,
    /// so the caller's rollback undoes the whole apply.
    /// </summary>
    [Test]
    public async Task ApplyExportedAttributeValuesAsync_InsideCallerTransaction_EnlistsAndRollsBackWithItAsync()
    {
        var (attr, csoId) = await SeedCsoAsync("old@example.com");
        var stamp = (Hash: Guid.NewGuid(), Fingerprint: Guid.NewGuid());
        await using (var stampCtx = NewContext())
            await new PostgresDataRepository(stampCtx).Sync.StampImportStateAsync([(csoId, stamp.Hash, stamp.Fingerprint)]);
        var existingId = await SingleValueIdAsync(csoId);

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var cso = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == csoId);
            var addition = new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = attr.Id, StringValue = "new@example.com"
            };

            await using var callerTransaction = await ctx.Database.BeginTransactionAsync();
            await repository.Sync.ApplyExportedAttributeValuesAsync([addition], [existingId], [csoId]);
            Assert.That(ctx.Database.CurrentTransaction, Is.SameAs(callerTransaction),
                "the apply must enlist in the caller's transaction, not end it");
            await callerTransaction.RollbackAsync();
        }

        await AssertObjectUnchangedAsync(csoId, existingId, "old@example.com", stamp);
    }

    /// <summary>
    /// #2895: an object with no import stamp, as every provisioned object is, has nothing for the import-state update to
    /// clear, so its row is not rewritten (its row version is unchanged); the values are still applied.
    /// </summary>
    [Test]
    public async Task ApplyExportedAttributeValuesAsync_UnstampedCso_DoesNotRewriteTheObjectRowAsync()
    {
        var (attr, csoId) = await SeedCsoAsync("old@example.com");
        var rowVersionBefore = await RowVersionAsync(csoId);

        await using (var ctx = NewContext())
        {
            var existing = await ctx.ConnectedSystemObjectAttributeValues.AsNoTracking()
                .SingleAsync(av => av.ConnectedSystemObject.Id == csoId);
            var cso = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == csoId);
            var addition = new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = attr.Id, StringValue = "new@example.com"
            };
            await new PostgresDataRepository(ctx).Sync.ApplyExportedAttributeValuesAsync([addition], [existing.Id], [csoId]);
        }

        await using var verify = NewContext();
        var storedValues = await verify.ConnectedSystemObjectAttributeValues.AsNoTracking()
            .Where(av => av.ConnectedSystemObject.Id == csoId).Select(av => av.StringValue).ToListAsync();
        var storedCso = await verify.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == csoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storedValues, Is.EquivalentTo(new[] { "new@example.com" }));
            Assert.That(storedCso.ImportStateHash, Is.Null);
            Assert.That(storedCso.ImportStateFingerprint, Is.Null);
            Assert.That(await RowVersionAsync(csoId), Is.EqualTo(rowVersionBefore), "an update with nothing to clear must not rewrite the row");
        }
    }

    /// <summary>
    /// #2895: the same for the external ID update after a successful export (<c>UpdateConnectedSystemObjectsWithNewAttributeValuesAsync</c>):
    /// an unstamped object's row is not rewritten, a stamped one is cleared as before, and both get their new values.
    /// </summary>
    [Test]
    public async Task UpdateConnectedSystemObjectsWithNewAttributeValuesAsync_UnstampedAndStampedCsos_RewritesOnlyTheStampedRowAsync()
    {
        var (attr, unstampedId) = await SeedCsoAsync();
        Guid stampedId;
        await using (var seed = NewContext())
        {
            var template = await seed.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == unstampedId);
            stampedId = Guid.NewGuid();
            seed.ConnectedSystemObjects.Add(new ConnectedSystemObject
            {
                Id = stampedId, ConnectedSystemId = template.ConnectedSystemId, TypeId = template.TypeId,
                Status = ConnectedSystemObjectStatus.Normal, ExternalIdAttributeId = attr.Id
            });
            await seed.SaveChangesAsync();
        }
        await using (var stampCtx = NewContext())
            await new PostgresDataRepository(stampCtx).Sync.StampImportStateAsync([(stampedId, Guid.NewGuid(), Guid.NewGuid())]);

        var unstampedVersionBefore = await RowVersionAsync(unstampedId);
        var stampedVersionBefore = await RowVersionAsync(stampedId);

        await using (var ctx = NewContext())
        {
            var objects = await ctx.ConnectedSystemObjects.AsNoTracking().Where(c => c.Id == unstampedId || c.Id == stampedId).ToListAsync();
            var updates = objects.Select(cso => (cso, new List<ConnectedSystemObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = attr.Id, StringValue = $"{cso.Id}@example.com" }
            })).ToList();
            await new PostgresDataRepository(ctx).Sync.UpdateConnectedSystemObjectsWithNewAttributeValuesAsync(updates);
        }

        await using var verify = NewContext();
        var stored = await verify.ConnectedSystemObjects.AsNoTracking().Include(c => c.AttributeValues)
            .Where(c => c.Id == unstampedId || c.Id == stampedId).ToDictionaryAsync(c => c.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Values.Select(c => c.AttributeValues.Single().StringValue),
                Is.EquivalentTo(new[] { $"{unstampedId}@example.com", $"{stampedId}@example.com" }));
            Assert.That(stored.Values.Select(c => c.ImportStateHash), Is.All.Null);
            Assert.That(stored.Values.Select(c => c.ImportStateFingerprint), Is.All.Null);
            Assert.That(await RowVersionAsync(unstampedId), Is.EqualTo(unstampedVersionBefore),
                "an unstamped object has nothing to clear, so its row must not be rewritten");
            Assert.That(await RowVersionAsync(stampedId), Is.Not.EqualTo(stampedVersionBefore), "a stamped object's stamp is cleared");
        }
    }

    /// <summary>
    /// The row's version (<c>xmin</c>): any rewrite of the row, even one setting the values it already holds, changes it.
    /// </summary>
    private async Task<long> RowVersionAsync(Guid csoId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            @"SELECT xmin::text::bigint FROM ""ConnectedSystemObjects"" WHERE ""Id"" = @id", connection);
        command.Parameters.AddWithValue("id", csoId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Guid> SingleValueIdAsync(Guid csoId)
    {
        await using var verify = NewContext();
        return await verify.ConnectedSystemObjectAttributeValues.AsNoTracking()
            .Where(av => av.ConnectedSystemObject.Id == csoId).Select(av => av.Id).SingleAsync();
    }

    private async Task AssertObjectUnchangedAsync(Guid csoId, Guid expectedValueId, string expectedValue, (Guid Hash, Guid Fingerprint) stamp)
    {
        await using var verify = NewContext();
        var storedValues = await verify.ConnectedSystemObjectAttributeValues.AsNoTracking()
            .Where(av => av.ConnectedSystemObject.Id == csoId).ToListAsync();
        var storedCso = await verify.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == csoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storedValues.Select(v => (v.Id, v.StringValue)), Is.EquivalentTo(new[] { (expectedValueId, (string?)expectedValue) }),
                "the superseded value must still be present and nothing new written");
            Assert.That(storedCso.ImportStateHash, Is.EqualTo(stamp.Hash), "the import stamp must be left as it was");
            Assert.That(storedCso.ImportStateFingerprint, Is.EqualTo(stamp.Fingerprint), "the import stamp must be left as it was");
        }
    }

    /// <summary>
    /// Seeds a Connected System with one USER object type carrying a single-valued Reference
    /// attribute, plus a "referenced" CSO (the resolution target) and a "source" CSO (the one the
    /// Pending Export is against).
    /// </summary>
    private async Task<(ConnectedSystemObjectTypeAttribute ReferenceAttribute, ConnectedSystemObject ReferencedCso, ConnectedSystemObject SourceCso)> SeedReferenceScenarioAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Target System", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var managerAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "manager", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(managerAttr);
        seed.AddRange(connectorDefinition, system, csType, managerAttr);
        await seed.SaveChangesAsync();

        var referencedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal
        };
        var sourceCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal
        };
        seed.AddRange(referencedCso, sourceCso);
        await seed.SaveChangesAsync();

        return (managerAttr, referencedCso, sourceCso);
    }

    /// <summary>
    /// SPEC-1079B RED test 1 (persistence round-trip): <c>ResolvedReferenceCsoId</c> must survive a
    /// real database round-trip through <c>UpdatePendingExportsAsync</c> (the path
    /// <c>ExportExecutionServer.ProcessDeferredExportsAsync</c> uses to persist a just-resolved
    /// reference before the deferred batch executes, and the path <c>ProcessBatchSuccessAsync</c>
    /// uses after every export attempt). Before the property is mapped this fails: EF's
    /// <c>[NotMapped]</c> attribute means the column is never written, so a fresh reload sees null.
    /// </summary>
    [Test]
    public async Task UpdatePendingExportsAsync_PersistsResolvedReferenceCsoId_RoundTripsOnFreshReloadAsync()
    {
        var (managerAttr, referencedCso, sourceCso) = await SeedReferenceScenarioAsync();

        await using var seed = NewContext();
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = sourceCso.ConnectedSystemId,
            ConnectedSystemObjectId = sourceCso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        var change = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = managerAttr.Id,
            ChangeType = PendingExportAttributeChangeType.Update,
            UnresolvedReferenceValue = Guid.NewGuid().ToString(),
            Status = PendingExportAttributeChangeStatus.Pending
        };
        pendingExport.AttributeValueChanges.Add(change);
        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        // Act: simulate reference resolution (TryResolveReferencesFromLookup) and persist exactly
        // as ExportExecutionServer does - StringValue set, UnresolvedReferenceValue cleared,
        // ResolvedReferenceCsoId stamped - via UpdatePendingExportsAsync.
        change.StringValue = "CN=Manager,DC=test";
        change.UnresolvedReferenceValue = null;
        change.ResolvedReferenceCsoId = referencedCso.Id;

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        await repository.Sync.UpdatePendingExportsAsync([pendingExport]);

        await using var verify = NewContext();
        var reloadedChange = await verify.PendingExportAttributeValueChanges.AsNoTracking()
            .SingleAsync(c => c.Id == change.Id);

        Assert.That(reloadedChange.ResolvedReferenceCsoId, Is.EqualTo(referencedCso.Id),
            "ResolvedReferenceCsoId must survive a real database round-trip through UpdatePendingExportsAsync");
        Assert.That(reloadedChange.StringValue, Is.EqualTo("CN=Manager,DC=test"));
    }

    /// <summary>
    /// SPEC-1079B RED test 2 (insert path): <c>ResolvedReferenceCsoId</c> must also survive the
    /// initial COPY binary import (<c>CreatePendingExportsAsync</c> -&gt;
    /// <c>BulkInsertPendingExportAttributeValueChangesOnConnectionAsync</c>), covering a change created
    /// with the id already set (for example a change built fresh from an already-resolved value).
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_PersistsResolvedReferenceCsoId_RoundTripsOnFreshReloadAsync()
    {
        var (managerAttr, referencedCso, sourceCso) = await SeedReferenceScenarioAsync();

        var change = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = managerAttr.Id,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "CN=Manager,DC=test",
            ResolvedReferenceCsoId = referencedCso.Id,
            Status = PendingExportAttributeChangeStatus.Pending
        };
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = sourceCso.ConnectedSystemId,
            ConnectedSystemObjectId = sourceCso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = [change]
        };

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        await repository.Sync.CreatePendingExportsAsync([pendingExport]);

        await using var verify = NewContext();
        var reloadedChange = await verify.PendingExportAttributeValueChanges.AsNoTracking()
            .SingleAsync(c => c.Id == change.Id);

        Assert.That(reloadedChange.ResolvedReferenceCsoId, Is.EqualTo(referencedCso.Id),
            "ResolvedReferenceCsoId must survive the initial insert round-trip through CreatePendingExportsAsync");
    }
}
