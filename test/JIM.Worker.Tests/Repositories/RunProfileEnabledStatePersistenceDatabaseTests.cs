// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of a Run Profile's enabled state (#2740): that every Run Profile which existed before
/// the state did is enabled, that the three fields survive a save from a context configured as JIM.Web runs it
/// (<c>NoTracking</c>), and that the stored state can be read past a tracked copy an update has already edited.
/// </summary>
/// <remarks>
/// The repository's update copies each field onto a tracked instance by hand, and one left out is accepted, reported as
/// saved and never written; the in-memory provider cannot tell. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class RunProfileEnabledStatePersistenceDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext(QueryTrackingBehavior tracking = QueryTrackingBehavior.NoTracking)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(tracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Run Profile enabled state tests.");

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

    [Test]
    public async Task RunProfileWrittenWithoutTheEnabledState_ReadsBackEnabledAsync()
    {
        // A row written as every Run Profile was before the state existed: the migration's default is what makes it
        // enabled, so no Run Profile stops running on upgrade.
        var systemId = await SeedConnectedSystemAsync();
        await using (var ctx = NewContext())
        {
            await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""ConnectedSystemRunProfiles"" (""Name"", ""Created"", ""CreatedByType"", ""LastUpdatedByType"", ""ConnectedSystemId"", ""RunType"", ""PageSize"")
                VALUES ('Full Import', now(), 0, 0, {systemId}, 1, 100)");
        }

        await using var verify = NewContext();
        var runProfile = await verify.ConnectedSystemRunProfiles.SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runProfile.Enabled, Is.True, "an existing Run Profile migrates enabled");
            Assert.That(runProfile.DisabledReason, Is.Null);
            Assert.That(runProfile.DisabledPartitionName, Is.Null);
        }
    }

    [Test]
    public async Task CreateConnectedSystemRunProfileAsync_Disabled_PersistsDisabledAsync()
    {
        // A database default of true must not swallow an explicit false on insert.
        var systemId = await SeedConnectedSystemAsync();
        await using (var ctx = NewContext())
        {
            await new PostgresDataRepository(ctx).ConnectedSystems.CreateConnectedSystemRunProfileAsync(new ConnectedSystemRunProfile
            {
                Name = "Full Import",
                ConnectedSystemId = systemId,
                RunType = ConnectedSystemRunType.FullImport,
                Enabled = false,
                DisabledReason = ConnectedSystemRunProfileDisabledReason.Manual
            });
        }

        await using var verify = NewContext();
        var runProfile = await verify.ConnectedSystemRunProfiles.SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(runProfile.Enabled, Is.False);
            Assert.That(runProfile.DisabledReason, Is.EqualTo(ConnectedSystemRunProfileDisabledReason.Manual));
        }
    }

    [Test]
    public async Task UpdateConnectedSystemRunProfileAsync_DisabledState_PersistsEveryFieldFromANoTrackingContextAsync()
    {
        var systemId = await SeedConnectedSystemAsync();
        var runProfileId = await SeedRunProfileAsync(systemId);

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var runProfile = (await repository.ConnectedSystems.GetConnectedSystemRunProfilesAsync(systemId)).Single();
            runProfile.Enabled = false;
            runProfile.DisabledReason = ConnectedSystemRunProfileDisabledReason.PartitionRemoved;
            runProfile.DisabledPartitionName = "dc=glitterband,dc=local";
            await repository.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(runProfile);
        }

        await using var verify = NewContext();
        var persisted = await verify.ConnectedSystemRunProfiles.SingleAsync(rp => rp.Id == runProfileId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.Enabled, Is.False, "the update must write Enabled, not just accept it");
            Assert.That(persisted.DisabledReason, Is.EqualTo(ConnectedSystemRunProfileDisabledReason.PartitionRemoved));
            Assert.That(persisted.DisabledPartitionName, Is.EqualTo("dc=glitterband,dc=local"));
        }
    }

    [Test]
    public async Task GetConnectedSystemRunProfileEnabledStateAsync_TrackedCopyAlreadyEdited_ReadsWhatIsStoredAsync()
    {
        // The Worker tracks by default: a query for the Run Profile there hands back the very instance the update edited,
        // so judging the update against it would compare the edit with itself.
        var systemId = await SeedConnectedSystemAsync();
        var runProfileId = await SeedRunProfileAsync(systemId);

        await using var ctx = NewContext(QueryTrackingBehavior.TrackAll);
        var repository = new PostgresDataRepository(ctx);
        var tracked = (await repository.ConnectedSystems.GetConnectedSystemRunProfilesAsync(systemId)).Single();
        tracked.Enabled = false;

        var stored = await repository.ConnectedSystems.GetConnectedSystemRunProfileEnabledStateAsync(runProfileId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.Enabled, Is.True, "the stored state, not the tracked copy's edit");
            Assert.That(stored.DisabledReason, Is.Null);
        }
    }

    [Test]
    public async Task GetConnectedSystemRunProfileEnabledStateAsync_NoSuchRunProfile_ReturnsNullAsync()
    {
        await using var ctx = NewContext();

        var stored = await new PostgresDataRepository(ctx).ConnectedSystems.GetConnectedSystemRunProfileEnabledStateAsync(404);

        Assert.That(stored, Is.Null);
    }

    [Test]
    public async Task UpdateConnectedSystemRunProfileAsync_FromTheServerOnANoTrackingContext_DisablesAndOnlyEnablesWithAScopeAsync()
    {
        // The portal's path: the server, on a context configured as JIM.Web runs it, judging each update against what is
        // stored. Disabling records that an administrator did it; a Run Profile a removed partition disabled is not
        // enabled again until the update says what it reads.
        var systemId = await SeedConnectedSystemAsync();
        var runProfileId = await SeedRunProfileAsync(systemId);
        var initiatorId = await SeedInitiatorAsync();

        await UpdateThroughTheServerAsync(systemId, initiatorId, rp => rp.Enabled = false);
        var disabled = await ReadAsync(runProfileId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(disabled.Enabled, Is.False);
            Assert.That(disabled.DisabledReason, Is.EqualTo(ConnectedSystemRunProfileDisabledReason.Manual));
        }

        // As a hierarchy refresh leaves it.
        await using (var ctx = NewContext())
        {
            await ctx.ConnectedSystemRunProfiles.Where(rp => rp.Id == runProfileId).ExecuteUpdateAsync(set => set
                .SetProperty(rp => rp.DisabledReason, ConnectedSystemRunProfileDisabledReason.PartitionRemoved)
                .SetProperty(rp => rp.DisabledPartitionName, "dc=glitterband,dc=local"));
        }

        Assert.ThrowsAsync<ArgumentException>(() => UpdateThroughTheServerAsync(systemId, initiatorId, rp => rp.Enabled = true),
            "enabling it with nothing chosen would have it read every selected partition");
        Assert.That((await ReadAsync(runProfileId)).Enabled, Is.False, "the refused update saved nothing");

        await UpdateThroughTheServerAsync(systemId, initiatorId, rp => rp.Enabled = true, allSelectedPartitions: true);
        var enabled = await ReadAsync(runProfileId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enabled.Enabled, Is.True);
            Assert.That(enabled.DisabledReason, Is.Null);
            Assert.That(enabled.DisabledPartitionName, Is.Null);
        }
    }

    private async Task UpdateThroughTheServerAsync(int systemId, Guid initiatorId, Action<ConnectedSystemRunProfile> edit, bool allSelectedPartitions = false)
    {
        await using var ctx = NewContext();
        var jim = new JIM.Application.JimApplication(new PostgresDataRepository(ctx));
        var initiator = await ctx.MetaverseObjects.SingleAsync(o => o.Id == initiatorId);
        var runProfile = (await jim.ConnectedSystems.GetConnectedSystemRunProfilesAsync(systemId)).Single();
        edit(runProfile);
        await jim.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(runProfile, initiator, allSelectedPartitions);
    }

    private async Task<ConnectedSystemRunProfile> ReadAsync(int runProfileId)
    {
        await using var verify = NewContext();
        return await verify.ConnectedSystemRunProfiles.SingleAsync(rp => rp.Id == runProfileId);
    }

    private async Task<Guid> SeedInitiatorAsync()
    {
        await using var seed = NewContext();
        // A change to configuration is recorded against a security principal or not at all.
        var userType = new JIM.Models.Core.MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        var initiator = new JIM.Models.Core.MetaverseObject { Type = userType, CachedDisplayName = "Test Administrator" };
        seed.AddRange(userType, initiator);
        await seed.SaveChangesAsync();
        return initiator.Id;
    }

    private async Task<int> SeedConnectedSystemAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", SupportsPartitions = true };
        var system = new ConnectedSystem { Name = "Test Directory", ConnectorDefinition = connectorDefinition };
        seed.AddRange(connectorDefinition, system);
        await seed.SaveChangesAsync();
        return system.Id;
    }

    private async Task<int> SeedRunProfileAsync(int systemId)
    {
        await using var seed = NewContext();
        var runProfile = new ConnectedSystemRunProfile { Name = "Full Import", ConnectedSystemId = systemId, RunType = ConnectedSystemRunType.FullImport };
        seed.ConnectedSystemRunProfiles.Add(runProfile);
        await seed.SaveChangesAsync();
        return runProfile.Id;
    }
}
