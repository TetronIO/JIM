// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for deleting a Connected System.
/// <para>
/// <see cref="JIM.PostgresData.Repositories.ConnectedSystemRepository.DeleteConnectedSystemAsync"/> is a
/// hand-ordered sequence of raw SQL statements running in one transaction. Every row that references something
/// the sequence deletes has to be removed first, or have its reference severed first; miss one and PostgreSQL
/// refuses that statement, the whole transaction rolls back, and the Connected System cannot be deleted at all.
/// The portal reports a save failure and the integration harness cannot clean up between runs.
/// </para>
/// <para>
/// Only a real provider can see this class of fault. The in-memory provider enforces no foreign keys, so the
/// delete appears to succeed there whatever the statement order, and it cannot execute the raw SQL in the first
/// place.
/// </para>
/// <para>
/// Each test seeds the one shape that provoked a real failure. The schema-level counterpart in
/// <see cref="JIM.Worker.Tests.Servers.DeletePathForeignKeyCoverageTests"/> generalises the property so a child
/// table added in a future release cannot silently reintroduce it.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemDeletionDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Connected System deletion tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .Options);

    /// <summary>
    /// Seeds a bare Connected System with one Connector Definition behind it, and returns both ids.
    /// </summary>
    private async Task<(int SystemId, int DefinitionId)> SeedSystemAsync(string suffix)
    {
        await using var ctx = NewContext();

        var definition = new ConnectorDefinition { Name = $"deletion-def-{suffix}" };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();

        var system = new ConnectedSystem { Name = $"deletion-system-{suffix}", ConnectorDefinitionId = definition.Id };
        ctx.ConnectedSystems.Add(system);
        await ctx.SaveChangesAsync();

        return (system.Id, definition.Id);
    }

    /// <summary>
    /// Seeds a Connected System with one Partition and a three-deep Container hierarchy, mirroring the shape an
    /// Active Directory hierarchy import produces (OU=Corp &gt; OU=Users &gt; OU=Sales). Only the top Container
    /// belongs to the Partition; the two below it hang off their parent, exactly as the import writes them.
    /// </summary>
    private async Task<int> SeedSystemWithNestedContainersAsync(string suffix)
    {
        var (systemId, _) = await SeedSystemAsync(suffix);

        await using var ctx = NewContext();

        // The Partition reaches its system by navigation rather than by a foreign-key property, so the system is
        // attached to this context before it is used.
        var system = await ctx.ConnectedSystems.AsTracking().SingleAsync(cs => cs.Id == systemId);

        var partition = new ConnectedSystemPartition
        {
            ConnectedSystem = system,
            ExternalId = $"DC=panoply,DC=local-{suffix}",
            Name = "DC=panoply,DC=local"
        };
        ctx.ConnectedSystemPartitions.Add(partition);
        await ctx.SaveChangesAsync();

        var top = new ConnectedSystemContainer { PartitionId = partition.Id, Name = "Corp", ExternalId = $"OU=Corp,DC=panoply,DC=local-{suffix}" };
        ctx.ConnectedSystemContainers.Add(top);
        await ctx.SaveChangesAsync();

        var middle = new ConnectedSystemContainer { ParentContainerId = top.Id, Name = "Users", ExternalId = $"OU=Users,OU=Corp,DC=panoply,DC=local-{suffix}" };
        ctx.ConnectedSystemContainers.Add(middle);
        await ctx.SaveChangesAsync();

        var leaf = new ConnectedSystemContainer { ParentContainerId = middle.Id, Name = "Sales", ExternalId = $"OU=Sales,OU=Users,OU=Corp,DC=panoply,DC=local-{suffix}" };
        ctx.ConnectedSystemContainers.Add(leaf);
        await ctx.SaveChangesAsync();

        return systemId;
    }

    /// <summary>
    /// Seeds a Connected System that has been configured for Password Synchronisation, which is the ordinary
    /// state of any system administrators have pointed passwords at.
    /// </summary>
    private async Task<int> SeedSystemWithPasswordSynchronisationAsync(string suffix)
    {
        var (systemId, _) = await SeedSystemAsync(suffix);

        await using var ctx = NewContext();

        var objectType = new ConnectedSystemObjectType { ConnectedSystemId = systemId, Name = "user", Selected = true };
        ctx.ConnectedSystemObjectTypes.Add(objectType);
        await ctx.SaveChangesAsync();

        ctx.ConnectedSystemPasswordSynchronisations.Add(new ConnectedSystemPasswordSynchronisation
        {
            ConnectedSystemId = systemId,
            TargetObjectTypeId = objectType.Id,
            Enabled = true
        });
        await ctx.SaveChangesAsync();

        return systemId;
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_WithANestedContainerHierarchy_DeletesItAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var systemId = await SeedSystemWithNestedContainersAsync(suffix);

        await using (var deleteCtx = NewContext())
        {
            var repository = new PostgresDataRepository(deleteCtx);

            Assert.That(async () => await repository.ConnectedSystems.DeleteConnectedSystemAsync(systemId),
                Throws.Nothing,
                "a Connected System that imported a nested hierarchy must still be deletable; the descendants " +
                "of a Container carry no PartitionId, so a delete keyed on that alone strands them");
        }

        await using var assertCtx = NewContext();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await assertCtx.ConnectedSystems.AnyAsync(cs => cs.Id == systemId), Is.False,
                "the Connected System itself is gone");
            Assert.That(await assertCtx.ConnectedSystemContainers
                    .CountAsync(c => c.Name == "Corp" || c.Name == "Users" || c.Name == "Sales"),
                Is.Zero,
                "every Container in the hierarchy goes with it, not just the one the Partition owned");
        }
    }

    /// <summary>
    /// Password Synchronisation points at the Connected System Object Type holding the system's user accounts,
    /// and that reference is RESTRICT on purpose: deleting the Object Type on its own would leave the
    /// configuration aimed at nothing. Deleting the whole Connected System is the case the RESTRICT is not meant
    /// to stop, and the sequence deletes Object Types well before the system itself, so the configuration has to
    /// be removed ahead of them or the RESTRICT refuses the statement and the delete rolls back entirely.
    /// </summary>
    [Test]
    public async Task DeleteConnectedSystemAsync_WithPasswordSynchronisationConfigured_DeletesItAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var systemId = await SeedSystemWithPasswordSynchronisationAsync(suffix);

        await using (var deleteCtx = NewContext())
        {
            var repository = new PostgresDataRepository(deleteCtx);

            Assert.That(async () => await repository.ConnectedSystems.DeleteConnectedSystemAsync(systemId),
                Throws.Nothing,
                "configuring Password Synchronisation must not make a Connected System undeletable; the " +
                "configuration's reference to the target Object Type is RESTRICT, so it has to go before the " +
                "Object Types do");
        }

        await using var assertCtx = NewContext();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await assertCtx.ConnectedSystems.AnyAsync(cs => cs.Id == systemId), Is.False,
                "the Connected System itself is gone");
            Assert.That(await assertCtx.ConnectedSystemPasswordSynchronisations
                    .AnyAsync(ps => ps.ConnectedSystemId == systemId),
                Is.False,
                "its Password Synchronisation configuration goes with it, rather than being left orphaned");
        }
    }
}
