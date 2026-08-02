// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the initial-password provisioning schema (#1121).
/// <para>
/// Two things here can only be proved against a real database. Pending Exports are written by raw SQL that
/// bypasses the EF model, so a column added to the model but missed by the writer, or written in the wrong
/// position, persists as null with no error anywhere; the in-memory provider goes through EF and cannot see it.
/// And the generator settings are stored as owned columns on the initial-password table, a mapping the in-memory
/// provider does not exercise as it would in production.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c>
/// fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is absent. Do NOT run this fixture outside the sanctioned
/// scratch-database workflow: <c>SetUp</c> TRUNCATEs every table.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class InitialPasswordProvisioningDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL initial password provisioning tests.");

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
    /// The provisioning rule survives the raw-SQL write path that stages Pending Exports in bulk.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_RecordsTheProvisioningRuleAsync()
    {
        var (systemId, syncRuleId) = await SeedSystemAndRuleAsync();

        await using var write = NewContext();
        var repository = new PostgresDataRepository(write);
        var exportId = Guid.NewGuid();

        await repository.Sync.CreatePendingExportsAsync([
            new PendingExport
            {
                Id = exportId,
                ConnectedSystemId = systemId,
                ChangeType = PendingExportChangeType.Create,
                Status = PendingExportStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                ProvisioningSyncRuleId = syncRuleId
            }
        ]);

        await using var verify = NewContext();
        var stored = await verify.PendingExports.AsNoTracking().SingleAsync(pe => pe.Id == exportId);
        Assert.That(stored.ProvisioningSyncRuleId, Is.EqualTo(syncRuleId));
    }

    /// <summary>
    /// An export with no provisioning rule stores a null rather than failing the foreign key, which is what
    /// every update and delete looks like.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_WithNoProvisioningRule_StoresNullAsync()
    {
        var (systemId, _) = await SeedSystemAndRuleAsync();

        await using var write = NewContext();
        var repository = new PostgresDataRepository(write);
        var exportId = Guid.NewGuid();

        await repository.Sync.CreatePendingExportsAsync([
            new PendingExport
            {
                Id = exportId,
                ConnectedSystemId = systemId,
                ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Pending,
                CreatedAt = DateTime.UtcNow
            }
        ]);

        await using var verify = NewContext();
        var stored = await verify.PendingExports.AsNoTracking().SingleAsync(pe => pe.Id == exportId);
        Assert.That(stored.ProvisioningSyncRuleId, Is.Null);
    }

    /// <summary>
    /// Deleting a Synchronisation Rule leaves already-staged exports in place, with the link cleared. The
    /// alternative, cascading, would discard work the target system is still waiting for.
    /// </summary>
    [Test]
    public async Task DeletingTheProvisioningRule_KeepsTheExportAndClearsTheLinkAsync()
    {
        var (systemId, syncRuleId) = await SeedSystemAndRuleAsync();
        var exportId = Guid.NewGuid();

        await using (var write = NewContext())
        {
            var repository = new PostgresDataRepository(write);
            await repository.Sync.CreatePendingExportsAsync([
                new PendingExport
                {
                    Id = exportId,
                    ConnectedSystemId = systemId,
                    ChangeType = PendingExportChangeType.Create,
                    Status = PendingExportStatus.Pending,
                    CreatedAt = DateTime.UtcNow,
                    ProvisioningSyncRuleId = syncRuleId
                }
            ]);
        }

        await using (var delete = NewContext())
        {
            delete.SyncRules.Remove(await delete.SyncRules.SingleAsync(sr => sr.Id == syncRuleId));
            await delete.SaveChangesAsync();
        }

        await using var verify = NewContext();
        var stored = await verify.PendingExports.AsNoTracking().SingleOrDefaultAsync(pe => pe.Id == exportId);
        Assert.That(stored, Is.Not.Null, "deleting a Synchronisation Rule must not delete exports already staged for it");
        Assert.That(stored!.ProvisioningSyncRuleId, Is.Null);
    }

    /// <summary>
    /// Every generator setting round-trips, including the ones stored as owned columns. Asserted field by
    /// field, because a mapping that silently drops one would otherwise show up as passwords quietly not
    /// matching the configuration an administrator saved.
    /// </summary>
    [Test]
    public async Task SyncRuleInitialPassword_RoundTripsEverySettingAsync()
    {
        var (_, syncRuleId) = await SeedSystemAndRuleAsync();

        await using (var write = NewContext())
        {
            write.SyncRuleInitialPasswords.Add(new SyncRuleInitialPassword
            {
                SyncRuleId = syncRuleId,
                Enabled = true,
                Source = InitialPasswordSource.Custom,
                ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires,
                EnableAccount = false,
                CustomPolicy = new PasswordGenerationPolicy
                {
                    Style = PasswordGenerationStyle.Words,
                    Length = 21,
                    MinimumUppercase = 2,
                    MinimumLowercase = 3,
                    MinimumDigits = 4,
                    MinimumSymbols = 5,
                    PermittedSymbols = "@#!",
                    WordCount = 6,
                    WordSeparator = PasswordWordSeparator.Underscore,
                    WordCapitalisation = PasswordWordCapitalisation.FirstWordOnly,
                    AppendedDigitCount = 3,
                    AppendSymbol = true,
                    ExcludeAmbiguousCharacters = false
                }
            });
            await write.SaveChangesAsync();
        }

        await using var verify = NewContext();
        var stored = await verify.SyncRuleInitialPasswords.AsNoTracking().SingleAsync(ip => ip.SyncRuleId == syncRuleId);
        var policy = stored.CustomPolicy;
        Assert.Multiple(() =>
        {
            Assert.That(stored.Enabled, Is.True);
            Assert.That(stored.Source, Is.EqualTo(InitialPasswordSource.Custom));
            Assert.That(stored.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
            Assert.That(stored.EnableAccount, Is.False);
            Assert.That(policy.Style, Is.EqualTo(PasswordGenerationStyle.Words));
            Assert.That(policy.Length, Is.EqualTo(21));
            Assert.That(policy.MinimumUppercase, Is.EqualTo(2));
            Assert.That(policy.MinimumLowercase, Is.EqualTo(3));
            Assert.That(policy.MinimumDigits, Is.EqualTo(4));
            Assert.That(policy.MinimumSymbols, Is.EqualTo(5));
            Assert.That(policy.PermittedSymbols, Is.EqualTo("@#!"));
            Assert.That(policy.WordCount, Is.EqualTo(6));
            Assert.That(policy.WordSeparator, Is.EqualTo(PasswordWordSeparator.Underscore));
            Assert.That(policy.WordCapitalisation, Is.EqualTo(PasswordWordCapitalisation.FirstWordOnly));
            Assert.That(policy.AppendedDigitCount, Is.EqualTo(3));
            Assert.That(policy.AppendSymbol, Is.True);
            Assert.That(policy.ExcludeAmbiguousCharacters, Is.False);
        });
    }

    /// <summary>
    /// Deleting a Synchronisation Rule takes its initial-password configuration with it: the configuration
    /// describes how that rule provisions and means nothing without it.
    /// </summary>
    [Test]
    public async Task DeletingTheRule_RemovesItsInitialPasswordConfigurationAsync()
    {
        var (_, syncRuleId) = await SeedSystemAndRuleAsync();

        await using (var write = NewContext())
        {
            write.SyncRuleInitialPasswords.Add(new SyncRuleInitialPassword { SyncRuleId = syncRuleId, Enabled = true });
            await write.SaveChangesAsync();
        }

        await using (var delete = NewContext())
        {
            delete.SyncRules.Remove(await delete.SyncRules.SingleAsync(sr => sr.Id == syncRuleId));
            await delete.SaveChangesAsync();
        }

        await using var verify = NewContext();
        Assert.That(await verify.SyncRuleInitialPasswords.AnyAsync(), Is.False);
    }

    /// <summary>
    /// Loading a Synchronisation Rule brings its initial-password configuration with it.
    /// <para>
    /// This is a guard against a silent failure rather than a plumbing check. The configuration snapshot that
    /// drives change history reads this navigation, and an unloaded navigation looks exactly like an
    /// unconfigured one: without the Include, every history entry would faithfully record that the initial
    /// password was switched off, no matter what the administrator had actually set.
    /// </para>
    /// </summary>
    [Test]
    public async Task GetSyncRuleAsync_LoadsTheInitialPasswordConfigurationAsync()
    {
        var (_, syncRuleId) = await SeedSystemAndRuleAsync();

        await using (var write = NewContext())
        {
            write.SyncRuleInitialPasswords.Add(new SyncRuleInitialPassword
            {
                SyncRuleId = syncRuleId,
                Enabled = true,
                Source = InitialPasswordSource.Custom
            });
            await write.SaveChangesAsync();
        }

        await using var read = NewContext();
        var rule = await new PostgresDataRepository(read).ConnectedSystems.GetSyncRuleAsync(syncRuleId);

        Assert.That(rule, Is.Not.Null);
        Assert.That(rule!.InitialPassword, Is.Not.Null,
            "the configuration snapshot reads this navigation; unloaded is indistinguishable from unconfigured");
        Assert.That(rule.InitialPassword!.Source, Is.EqualTo(InitialPasswordSource.Custom));
    }

    /// <summary>
    /// Every field of a staged initial password survives the raw-SQL insert, asserted one by one.
    /// <para>
    /// The completeness test beside this proves the column list matches the model; it cannot prove the writer
    /// puts the right value in each position, nor that each nullable parameter carries the right PostgreSQL
    /// type. Both faults are invisible to the in-memory provider, and both would show up in production as an
    /// account whose outstanding password quietly lost its reason or its expiry.
    /// </para>
    /// </summary>
    [Test]
    public async Task StageInitialPasswordsAsync_RoundTripsEveryFieldAsync()
    {
        var (systemId, syncRuleId, csoId) = await SeedSystemRuleAndAccountAsync();

        var id = Guid.NewGuid();
        var createdAt = DateTime.UtcNow.AddMinutes(-30);
        var lastAttemptedAt = DateTime.UtcNow.AddMinutes(-5);
        var expiresAt = DateTime.UtcNow.AddDays(7);

        await using (var write = NewContext())
        {
            await new PostgresDataRepository(write).Sync.StageInitialPasswordsAsync([
                new PendingInitialPassword
                {
                    Id = id,
                    ConnectedSystemObjectId = csoId,
                    ConnectedSystemId = systemId,
                    SyncRuleId = syncRuleId,
                    Status = PendingInitialPasswordStatus.Parked,
                    FailureReason = PasswordSetFailureReason.PolicyRejection,
                    TargetMessage = "The password does not meet the length, complexity or history requirements of the domain.",
                    AttemptCount = 3,
                    CreatedAt = createdAt,
                    LastAttemptedAt = lastAttemptedAt,
                    ExpiresAt = expiresAt
                }
            ]);
        }

        await using var verify = NewContext();
        var stored = await verify.PendingInitialPasswords.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Multiple(() =>
        {
            Assert.That(stored.ConnectedSystemObjectId, Is.EqualTo(csoId));
            Assert.That(stored.ConnectedSystemId, Is.EqualTo(systemId));
            Assert.That(stored.SyncRuleId, Is.EqualTo(syncRuleId));
            Assert.That(stored.Status, Is.EqualTo(PendingInitialPasswordStatus.Parked));
            Assert.That(stored.FailureReason, Is.EqualTo(PasswordSetFailureReason.PolicyRejection));
            Assert.That(stored.TargetMessage, Is.EqualTo("The password does not meet the length, complexity or history requirements of the domain."));
            Assert.That(stored.AttemptCount, Is.EqualTo(3));
            Assert.That(stored.CreatedAt, Is.EqualTo(createdAt).Within(TimeSpan.FromMilliseconds(1)));
            Assert.That(stored.LastAttemptedAt, Is.EqualTo(lastAttemptedAt).Within(TimeSpan.FromMilliseconds(1)));
            Assert.That(stored.ExpiresAt, Is.EqualTo(expiresAt).Within(TimeSpan.FromMilliseconds(1)));
        });
    }

    /// <summary>
    /// The nullable columns store nulls rather than tripping over their parameter types, which is what a
    /// freshly staged record looks like: never attempted, no reason yet, no expiry.
    /// </summary>
    [Test]
    public async Task StageInitialPasswordsAsync_WithNothingAttemptedYet_StoresNullsAsync()
    {
        var (systemId, _, csoId) = await SeedSystemRuleAndAccountAsync();

        var id = Guid.NewGuid();
        await using (var write = NewContext())
        {
            await new PostgresDataRepository(write).Sync.StageInitialPasswordsAsync([
                new PendingInitialPassword { Id = id, ConnectedSystemObjectId = csoId, ConnectedSystemId = systemId }
            ]);
        }

        await using var verify = NewContext();
        var stored = await verify.PendingInitialPasswords.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Multiple(() =>
        {
            Assert.That(stored.SyncRuleId, Is.Null);
            Assert.That(stored.FailureReason, Is.Null);
            Assert.That(stored.TargetMessage, Is.Null);
            Assert.That(stored.LastAttemptedAt, Is.Null);
            Assert.That(stored.ExpiresAt, Is.Null);
            Assert.That(stored.Status, Is.EqualTo(PendingInitialPasswordStatus.Pending));
            Assert.That(stored.AttemptCount, Is.Zero);
        });
    }

    /// <summary>
    /// Staging the same account twice leaves the first record untouched rather than failing, because
    /// re-running an export that already staged this work is an ordinary thing for an administrator to do, and
    /// the second row would be a second delivery racing the first to set a password on the same account.
    /// </summary>
    [Test]
    public async Task StageInitialPasswordsAsync_ForAnAccountAlreadyOutstanding_KeepsTheFirstRecordAsync()
    {
        var (systemId, syncRuleId, csoId) = await SeedSystemRuleAndAccountAsync();
        var firstId = Guid.NewGuid();

        await using (var write = NewContext())
        {
            var repository = new PostgresDataRepository(write);
            await repository.Sync.StageInitialPasswordsAsync([
                new PendingInitialPassword
                {
                    Id = firstId,
                    ConnectedSystemObjectId = csoId,
                    ConnectedSystemId = systemId,
                    SyncRuleId = syncRuleId,
                    AttemptCount = 2,
                    TargetMessage = "The directory was unreachable."
                }
            ]);

            await repository.Sync.StageInitialPasswordsAsync([
                new PendingInitialPassword
                {
                    Id = Guid.NewGuid(),
                    ConnectedSystemObjectId = csoId,
                    ConnectedSystemId = systemId,
                    SyncRuleId = syncRuleId
                }
            ]);
        }

        await using var verify = NewContext();
        var stored = await verify.PendingInitialPasswords.AsNoTracking().SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(stored.Id, Is.EqualTo(firstId));
            Assert.That(stored.AttemptCount, Is.EqualTo(2), "the existing record's progress must not be reset by re-staging");
            Assert.That(stored.TargetMessage, Is.EqualTo("The directory was unreachable."));
        });
    }

    /// <summary>
    /// Deleting the account takes its outstanding password with it. A password owed to an object that no
    /// longer exists is not work anybody can do.
    /// </summary>
    [Test]
    public async Task DeletingTheAccount_RemovesItsOutstandingPasswordAsync()
    {
        var (systemId, syncRuleId, csoId) = await SeedSystemRuleAndAccountAsync();

        await using (var write = NewContext())
        {
            await new PostgresDataRepository(write).Sync.StageInitialPasswordsAsync([
                new PendingInitialPassword { ConnectedSystemObjectId = csoId, ConnectedSystemId = systemId, SyncRuleId = syncRuleId }
            ]);
        }

        await using (var delete = NewContext())
        {
            delete.ConnectedSystemObjects.Remove(await delete.ConnectedSystemObjects.SingleAsync(cso => cso.Id == csoId));
            await delete.SaveChangesAsync();
        }

        await using var verify = NewContext();
        Assert.That(await verify.PendingInitialPasswords.AnyAsync(), Is.False);
    }

    /// <summary>
    /// Everything an attempt can change round-trips, and nothing it cannot change is touched.
    /// <para>
    /// The update is raw SQL, so a value written into the wrong column or with the wrong PostgreSQL type
    /// persists without an error anywhere, and the in-memory provider goes through EF and cannot see it. The
    /// second half of the assertion is the exclusion list made real: which account the password is owed to and
    /// when the work was staged are facts about how the record came to exist, and an attempt does not change
    /// them.
    /// </para>
    /// </summary>
    [Test]
    public async Task RecordInitialPasswordAttemptsAsync_PersistsTheAttemptAndLeavesItsOriginsAloneAsync()
    {
        var (systemId, syncRuleId, csoId) = await SeedSystemRuleAndAccountAsync();
        var id = Guid.NewGuid();
        var createdAt = DateTime.UtcNow.AddHours(-2);

        await using (var stage = NewContext())
        {
            await new PostgresDataRepository(stage).Sync.StageInitialPasswordsAsync([
                new PendingInitialPassword
                {
                    Id = id,
                    ConnectedSystemObjectId = csoId,
                    ConnectedSystemId = systemId,
                    SyncRuleId = syncRuleId,
                    CreatedAt = createdAt
                }
            ]);
        }

        var lastAttemptedAt = DateTime.UtcNow;
        var expiresAt = DateTime.UtcNow.AddDays(14);

        await using (var write = NewContext())
        {
            await new PostgresDataRepository(write).Sync.RecordInitialPasswordAttemptsAsync([
                new PendingInitialPassword
                {
                    Id = id,
                    Status = PendingInitialPasswordStatus.Parked,
                    FailureReason = PasswordSetFailureReason.PolicyRejection,
                    TargetMessage = "The password does not meet the complexity requirements of the domain.",
                    AttemptCount = 4,
                    LastAttemptedAt = lastAttemptedAt,
                    ExpiresAt = expiresAt
                }
            ]);
        }

        await using var verify = NewContext();
        var stored = await verify.PendingInitialPasswords.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Multiple(() =>
        {
            Assert.That(stored.Status, Is.EqualTo(PendingInitialPasswordStatus.Parked));
            Assert.That(stored.FailureReason, Is.EqualTo(PasswordSetFailureReason.PolicyRejection));
            Assert.That(stored.TargetMessage, Is.EqualTo("The password does not meet the complexity requirements of the domain."));
            Assert.That(stored.AttemptCount, Is.EqualTo(4));
            Assert.That(stored.LastAttemptedAt, Is.EqualTo(lastAttemptedAt).Within(TimeSpan.FromMilliseconds(1)));
            Assert.That(stored.ExpiresAt, Is.EqualTo(expiresAt).Within(TimeSpan.FromMilliseconds(1)));

            Assert.That(stored.ConnectedSystemObjectId, Is.EqualTo(csoId), "an attempt must not move the record to another account");
            Assert.That(stored.ConnectedSystemId, Is.EqualTo(systemId));
            Assert.That(stored.SyncRuleId, Is.EqualTo(syncRuleId));
            Assert.That(stored.CreatedAt, Is.EqualTo(createdAt).Within(TimeSpan.FromMilliseconds(1)), "when the work was staged is not something an attempt changes");
        });
    }

    /// <summary>
    /// A delivered password stops being outstanding, which is what makes the table a work list rather than a
    /// history of every account JIM has ever given a password to.
    /// </summary>
    [Test]
    public async Task DeleteInitialPasswordsAsync_RemovesTheRecordAsync()
    {
        var (systemId, syncRuleId, csoId) = await SeedSystemRuleAndAccountAsync();
        var id = Guid.NewGuid();

        await using (var stage = NewContext())
        {
            await new PostgresDataRepository(stage).Sync.StageInitialPasswordsAsync([
                new PendingInitialPassword { Id = id, ConnectedSystemObjectId = csoId, ConnectedSystemId = systemId, SyncRuleId = syncRuleId }
            ]);
        }

        await using (var delete = NewContext())
        {
            await new PostgresDataRepository(delete).Sync.DeleteInitialPasswordsAsync([id]);
        }

        await using var verify = NewContext();
        Assert.That(await verify.PendingInitialPasswords.AnyAsync(), Is.False);
    }

    /// <summary>
    /// The outstanding query brings the account with it, and excludes anything parked.
    /// <para>
    /// Both are invisible to the in-memory provider: it auto-tracks navigations, so a missing Include reads
    /// exactly like a present one, and the delivery pass would then have nothing to set a password on. The
    /// parked exclusion is what stops a target's final answer being re-asked on every export for ever.
    /// </para>
    /// </summary>
    [Test]
    public async Task GetOutstandingInitialPasswordsAsync_LoadsTheAccountAndSkipsParkedRecordsAsync()
    {
        var (systemId, syncRuleId, pendingCsoId) = await SeedSystemRuleAndAccountAsync();
        var parkedCsoId = await SeedAccountAsync(systemId);
        var parkedId = Guid.NewGuid();

        await using (var stage = NewContext())
        {
            var repository = new PostgresDataRepository(stage).Sync;
            await repository.StageInitialPasswordsAsync([
                new PendingInitialPassword { ConnectedSystemObjectId = pendingCsoId, ConnectedSystemId = systemId, SyncRuleId = syncRuleId },
                new PendingInitialPassword { Id = parkedId, ConnectedSystemObjectId = parkedCsoId, ConnectedSystemId = systemId, SyncRuleId = syncRuleId }
            ]);
            await repository.RecordInitialPasswordAttemptsAsync([
                new PendingInitialPassword { Id = parkedId, Status = PendingInitialPasswordStatus.Parked, AttemptCount = 1, LastAttemptedAt = DateTime.UtcNow }
            ]);
        }

        await using var read = NewContext();
        var outstanding = await new PostgresDataRepository(read).Sync.GetOutstandingInitialPasswordsAsync(systemId, 100);

        Assert.That(outstanding, Has.Count.EqualTo(1), "a parked record must not be handed back for another attempt");
        Assert.That(outstanding[0].ConnectedSystemObjectId, Is.EqualTo(pendingCsoId));
        Assert.That(outstanding[0].ConnectedSystemObject, Is.Not.Null,
            "the delivery pass sets the password on this object; without it there is nothing to deliver to");
    }

    private async Task<(int SystemId, int SyncRuleId, Guid CsoId)> SeedSystemRuleAndAccountAsync()
    {
        var (systemId, syncRuleId) = await SeedSystemAndRuleAsync();
        return (systemId, syncRuleId, await SeedAccountAsync(systemId));
    }

    private async Task<Guid> SeedAccountAsync(int systemId)
    {
        await using var seed = NewContext();
        var csType = await seed.ConnectedSystemObjectTypes.SingleAsync(t => t.ConnectedSystemId == systemId);
        var externalIdAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true,
            ConnectedSystemObjectType = csType
        };
        seed.Add(externalIdAttribute);
        await seed.SaveChangesAsync();

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

    private async Task<(int SystemId, int SyncRuleId)> SeedSystemAndRuleAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone Directory", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = false };
        seed.AddRange(connectorDefinition, system, csType, mvType);
        await seed.SaveChangesAsync();

        var syncRule = new SyncRule
        {
            Name = "Provision Users",
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id,
            MetaverseObjectTypeId = mvType.Id,
            ProvisionToConnectedSystem = true
        };
        seed.SyncRules.Add(syncRule);
        await seed.SaveChangesAsync();

        return (system.Id, syncRule.Id);
    }
}
