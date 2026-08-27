// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the reads and writes the Password Synchronisation delivery pass depends on
/// (#1119).
/// <para>
/// Two things here are structurally invisible to the unit suite. A Password Delivery task is stored table-per-
/// hierarchy alongside every other Worker Task, so whether it persists at all, and whether the de-duplication
/// read finds only tasks of its own type, are questions about the discriminator, and a mocked DbSet answers them
/// in LINQ-to-objects where the discriminator does not exist. The delivery pass's Connected System load is a
/// purpose-built Include set, and an Include that is missing shows up as a null navigation rather than an error;
/// the in-memory provider populates navigations from its identity map regardless of what was included.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c>
/// fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is absent. Do NOT run this fixture outside the sanctioned
/// scratch-database workflow: <c>SetUp</c> TRUNCATEs every table.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PasswordDeliveryReadsDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Password Delivery read tests.");

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

    private static Activity NewActivity() => new()
    {
        Id = Guid.NewGuid(),
        InitiatedByType = ActivityInitiatorType.System,
        InitiatedByName = "Password Synchronisation",
        TargetType = ActivityTargetType.PasswordSynchronisation,
        TargetOperationType = ActivityTargetOperationType.Execute
    };

    /// <summary>
    /// Queues a Password Delivery task through the repository, exactly as the tasking server does.
    /// </summary>
    private async Task<Guid> QueueDeliveryTaskAsync(int? connectedSystemId, WorkerTaskStatus status = WorkerTaskStatus.Queued)
    {
        var task = PasswordDeliveryWorkerTask.ForSystem("Password Synchronisation", connectedSystemId);
        task.Activity = NewActivity();
        task.Status = status;

        await using var ctx = NewContext();
        await new PostgresDataRepository(ctx).Tasking.CreateWorkerTaskAsync(task);
        return task.Id;
    }

    [Test]
    public async Task CreateWorkerTaskAsync_PasswordDeliveryTask_ActuallyPersistsAsync()
    {
        // The task type is stored table-per-hierarchy with every other Worker Task, and its case in the create
        // switch is the only thing that saves it. A case that adds without saving leaves the queue empty and the
        // worker with nothing to run, while every caller reports success.
        var id = await QueueDeliveryTaskAsync(connectedSystemId: 4);

        await using var verify = NewContext();
        var stored = await verify.WorkerTasks.AsNoTracking().OfType<PasswordDeliveryWorkerTask>().SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Id, Is.EqualTo(id));
            Assert.That(stored.ConnectedSystemId, Is.EqualTo(4));
            Assert.That(stored.Status, Is.EqualTo(WorkerTaskStatus.Queued));
            Assert.That(stored.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        }
    }

    [Test]
    public async Task CreateWorkerTaskAsync_EverySystem_PersistsANullConnectedSystemIdAsync()
    {
        await QueueDeliveryTaskAsync(connectedSystemId: null);

        await using var verify = NewContext();
        var stored = await verify.WorkerTasks.AsNoTracking().OfType<PasswordDeliveryWorkerTask>().SingleAsync();

        Assert.That(stored.ConnectedSystemId, Is.Null);
    }

    [Test]
    public async Task HasQueuedPasswordDeliveryTaskAsync_MatchesOnScopeAndStatusAsync()
    {
        await QueueDeliveryTaskAsync(connectedSystemId: 4);
        await QueueDeliveryTaskAsync(connectedSystemId: 7, status: WorkerTaskStatus.Processing);

        await using var ctx = NewContext();
        var tasking = new PostgresDataRepository(ctx).Tasking;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await tasking.HasQueuedPasswordDeliveryTaskAsync(4), Is.True);
            Assert.That(await tasking.HasQueuedPasswordDeliveryTaskAsync(5), Is.False,
                "A pass aimed at one system does nothing for another.");
            Assert.That(await tasking.HasQueuedPasswordDeliveryTaskAsync(null), Is.False,
                "A pass over one system leaves every other system undelivered.");
            Assert.That(await tasking.HasQueuedPasswordDeliveryTaskAsync(7), Is.False,
                "A pass already running may have read the queue before this work reached it.");
        }
    }

    [Test]
    public async Task HasQueuedPasswordDeliveryTaskAsync_UnscopedPass_CoversEverySystemAsync()
    {
        await QueueDeliveryTaskAsync(connectedSystemId: null);

        await using var ctx = NewContext();
        var tasking = new PostgresDataRepository(ctx).Tasking;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await tasking.HasQueuedPasswordDeliveryTaskAsync(null), Is.True);
            Assert.That(await tasking.HasQueuedPasswordDeliveryTaskAsync(4), Is.True);
        }
    }

    [Test]
    public async Task HasQueuedPasswordDeliveryTaskAsync_OnlySeesItsOwnTaskTypeAsync()
    {
        // The discriminator is what separates these rows, and a mocked DbSet has none.
        var reconciliation = new TemporalScopeReconciliationWorkerTask
        {
            Id = Guid.NewGuid(),
            InitiatedByType = ActivityInitiatorType.System,
            Status = WorkerTaskStatus.Queued,
            Activity = new Activity
            {
                Id = Guid.NewGuid(),
                InitiatedByType = ActivityInitiatorType.System,
                TargetType = ActivityTargetType.TemporalScopeReconciliation,
                TargetOperationType = ActivityTargetOperationType.Execute
            }
        };

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Tasking.CreateWorkerTaskAsync(reconciliation);

        await using var ctx = NewContext();
        var tasking = new PostgresDataRepository(ctx).Tasking;

        Assert.That(await tasking.HasQueuedPasswordDeliveryTaskAsync(null), Is.False);
    }

    [Test]
    public async Task GetConnectedSystemForPasswordDeliveryAsync_LoadsEverythingTheePassNeedsAsync()
    {
        int systemId;
        await using (var seed = NewContext())
        {
            var setting = new ConnectorDefinitionSetting { Name = "Server", Type = ConnectedSystemSettingType.String };
            var connectorDefinition = new ConnectorDefinition
            {
                Name = "Test Connector",
                BuiltIn = true,
                SupportsPasswordSet = true,
                Settings = [setting]
            };
            var system = new ConnectedSystem
            {
                Name = "Corporate AD",
                ConnectorDefinition = connectorDefinition,
                RequireSecureTransport = true
            };
            var objectType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            seed.AddRange(connectorDefinition, system, objectType);
            await seed.SaveChangesAsync();

            seed.Add(new ConnectedSystemSettingValue
            {
                ConnectedSystem = system,
                Setting = setting,
                StringValue = "dc.example.test"
            });
            seed.Add(new ConnectedSystemPasswordSynchronisation
            {
                ConnectedSystemId = system.Id,
                Enabled = true,
                TargetObjectTypeId = objectType.Id,
                MaxRetries = 4,
                RetryBackoffBase = TimeSpan.FromMinutes(3)
            });
            await seed.SaveChangesAsync();
            systemId = system.Id;
        }

        await using var ctx = NewContext();
        var loaded = await new PostgresDataRepository(ctx).ConnectedSystems.GetConnectedSystemForPasswordDeliveryAsync(systemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.ConnectorDefinition, Is.Not.Null, "The pass resolves the Connector from its definition's name.");
            Assert.That(loaded.ConnectorDefinition.Name, Is.EqualTo("Test Connector"));
            Assert.That(loaded.SettingValues, Has.Exactly(1).Items, "The Connector opens its password channel from these.");
            Assert.That(loaded.SettingValues[0].Setting, Is.Not.Null, "A setting value without its Setting cannot be matched by name.");
            Assert.That(loaded.SettingValues[0].Setting.Name, Is.EqualTo("Server"));
            Assert.That(loaded.PasswordSynchronisation, Is.Not.Null, "Without this the pass would treat a configured system as unconfigured.");
            Assert.That(loaded.PasswordSynchronisation!.Enabled, Is.True);
            Assert.That(loaded.PasswordSynchronisation.MaxRetries, Is.EqualTo(4));
            Assert.That(loaded.PasswordSynchronisation.RetryBackoffBase, Is.EqualTo(TimeSpan.FromMinutes(3)));
            Assert.That(loaded.RequireSecureTransport, Is.True,
                "The refusal is read off the Connected System itself, so the pass never needs an Include to find it.");
        }
    }

    [Test]
    public async Task GetConnectedSystemForPasswordDeliveryAsync_SystemDoesNotExist_ReturnsNullAsync()
    {
        await using var ctx = NewContext();

        var loaded = await new PostgresDataRepository(ctx).ConnectedSystems.GetConnectedSystemForPasswordDeliveryAsync(9999);

        Assert.That(loaded, Is.Null);
    }

    /// <summary>
    /// Seeds a Connected System configured for Password Synchronisation in the given state, with one pending
    /// password change due against it, and returns the system's id.
    /// </summary>
    private async Task<int> SeedSystemWithADueChangeAsync(string name, bool enabled)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = $"{name} Connector", SupportsPasswordSet = true };
        var system = new ConnectedSystem { Name = name, ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        // A real identity, because PendingPasswordChange.MetaverseObjectId is a foreign key: a fabricated Guid is
        // refused by the database, which is the whole reason these reads are covered against a real provider.
        var metaverseObjectType = new MetaverseObjectType { Name = $"User {name}", PluralName = $"Users {name}" };
        var metaverseObject = new MetaverseObject { Type = metaverseObjectType };
        seed.AddRange(connectorDefinition, system, objectType, metaverseObjectType, metaverseObject);
        await seed.SaveChangesAsync();

        seed.Add(new ConnectedSystemPasswordSynchronisation
        {
            ConnectedSystemId = system.Id,
            Enabled = enabled,
            TargetObjectTypeId = objectType.Id
        });
        seed.Add(new PendingPasswordChange
        {
            MetaverseObjectId = metaverseObject.Id,
            ConnectedSystemId = system.Id,
            EncryptedPassword = "protected",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await seed.SaveChangesAsync();

        return system.Id;
    }

    /// <summary>
    /// A switched-off system accumulates password changes rather than discarding them, and a delivery pass steps
    /// over it without touching them. Reporting it as having work due would therefore have the worker's idle
    /// sweep raise a delivery pass every minute, each recording an Activity for having done nothing, for as long
    /// as the system stayed off. The changes are held, not due.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemIdsWithDuePasswordChangesAsync_SkipsASystemThatIsSwitchedOffAsync()
    {
        var enabled = await SeedSystemWithADueChangeAsync("Corporate AD", enabled: true);
        await SeedSystemWithADueChangeAsync("Contractor LDAP", enabled: false);

        await using var ctx = NewContext();
        var systemIds = await new PostgresDataRepository(ctx).Sync
            .GetConnectedSystemIdsWithDuePasswordChangesAsync(DateTime.UtcNow);

        Assert.That(systemIds, Is.EqualTo(new[] { enabled }));
    }

    /// <summary>
    /// The other half of the same rule: switching the system on is what makes its accumulated changes due, with
    /// no change to the changes themselves. This is requirement 3's drain, read from the query that decides
    /// whether there is anything to drain.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemIdsWithDuePasswordChangesAsync_OnceEnabled_ReportsWhatAccumulatedAsync()
    {
        var systemId = await SeedSystemWithADueChangeAsync("Contractor LDAP", enabled: false);

        await using (var enable = NewContext())
        {
            var configuration = await enable.ConnectedSystemPasswordSynchronisations
                .SingleAsync(ps => ps.ConnectedSystemId == systemId);
            configuration.Enabled = true;
            await enable.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var systemIds = await new PostgresDataRepository(ctx).Sync
            .GetConnectedSystemIdsWithDuePasswordChangesAsync(DateTime.UtcNow);

        Assert.That(systemIds, Is.EqualTo(new[] { systemId }));
    }
}
