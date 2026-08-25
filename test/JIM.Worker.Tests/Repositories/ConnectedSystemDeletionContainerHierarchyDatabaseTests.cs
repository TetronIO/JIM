// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for deleting a Connected System whose directory hierarchy is nested.
/// <para>
/// Deleting a Connected System removed its Containers with a single statement keyed on PartitionId. A Container
/// discovered below another one carries no PartitionId of its own (it hangs off ParentContainerId), so that
/// statement deleted the top of each branch and left every descendant behind, still referencing a row that had
/// just gone. PostgreSQL refused the delete on the self-referencing foreign key and the whole transaction rolled
/// back, so a Connected System that had ever imported a nested hierarchy could not be deleted at all: the portal
/// reported a save failure and the integration harness could not clean up between runs.
/// </para>
/// <para>
/// Only a real provider can see this. The in-memory provider enforces no foreign keys, so the delete appears to
/// succeed there however the rows are ordered, and a hierarchy one level deep (which is all a unit fixture
/// usually builds) never reaches the case in the first place.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemDeletionContainerHierarchyDatabaseTests
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
    /// Seeds a Connected System with one Partition and a three-deep Container hierarchy, mirroring the shape an
    /// Active Directory hierarchy import produces (OU=Corp &gt; OU=Users &gt; OU=Sales). Only the top Container
    /// belongs to the Partition; the two below it hang off their parent, exactly as the import writes them.
    /// </summary>
    private async Task<int> SeedSystemWithNestedContainersAsync(string suffix)
    {
        await using var ctx = NewContext();

        var definition = new ConnectorDefinition { Name = $"deletion-def-{suffix}" };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();

        var system = new ConnectedSystem { Name = $"deletion-system-{suffix}", ConnectorDefinitionId = definition.Id };
        ctx.ConnectedSystems.Add(system);
        await ctx.SaveChangesAsync();

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

        return system.Id;
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
}
