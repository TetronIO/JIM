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
