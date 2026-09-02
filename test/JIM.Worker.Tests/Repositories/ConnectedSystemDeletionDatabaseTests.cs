// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
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

    /// <summary>
    /// The deletion sequence severs the audit foreign keys in the DATABASE with raw SQL
    /// (Activities.ConnectedSystemId, .SyncRuleId, .ConnectedSystemRunProfileId), but the worker holds the
    /// task's Activity TRACKED on the same long-lived DbContext, and the completion write that follows the
    /// deletion marks the whole entity Modified (<c>UpdateDetachedSafe</c>). Without a tracker fix-up the
    /// tracked instance re-asserts the deleted system id and PostgreSQL refuses with 23503
    /// (FK_Activities_ConnectedSystems_ConnectedSystemId), which is exactly how the first Synchronised
    /// Deprovisioning run (#809) died after "system deleted", and it then poisons every later
    /// SaveChangesAsync on the context, including FailActivityWithErrorAsync and the task-row completion,
    /// leaving the task stuck InProgress and the worker queue wedged.
    /// </summary>
    [Test]
    public async Task DeleteConnectedSystemAsync_WithATrackedActivityReferencingTheSystem_DoesNotReassertTheSeveredForeignKeysAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (systemId, _) = await SeedSystemAsync(suffix);

        // Seed the graph the severing statements key on: a Metaverse Object Type and Connected System
        // Object Type backing a Synchronisation Rule, plus a Run Profile.
        int syncRuleId;
        int runProfileId;
        Guid activityId;
        await using (var seedCtx = NewContext())
        {
            var mvoType = new MetaverseObjectType { Name = $"deletion-mvo-type-{suffix}", PluralName = $"deletion-mvo-types-{suffix}" };
            seedCtx.MetaverseObjectTypes.Add(mvoType);
            var csoType = new ConnectedSystemObjectType { ConnectedSystemId = systemId, Name = "user", Selected = true };
            seedCtx.ConnectedSystemObjectTypes.Add(csoType);
            await seedCtx.SaveChangesAsync();

            var syncRule = new SyncRule
            {
                Name = $"deletion-rule-{suffix}",
                ConnectedSystemId = systemId,
                ConnectedSystemObjectTypeId = csoType.Id,
                MetaverseObjectTypeId = mvoType.Id,
                Direction = SyncRuleDirection.Import
            };
            seedCtx.SyncRules.Add(syncRule);
            var runProfile = new ConnectedSystemRunProfile
            {
                Name = $"deletion-profile-{suffix}",
                ConnectedSystemId = systemId,
                RunType = ConnectedSystemRunType.FullSynchronisation
            };
            seedCtx.ConnectedSystemRunProfiles.Add(runProfile);
            await seedCtx.SaveChangesAsync();
            syncRuleId = syncRule.Id;
            runProfileId = runProfile.Id;

            // The worst-case Activity: it carries every foreign key the deletion severs.
            var activity = new Activity
            {
                Id = Guid.NewGuid(),
                TargetType = ActivityTargetType.ConnectedSystem,
                TargetOperationType = ActivityTargetOperationType.Delete,
                Status = ActivityStatus.InProgress,
                ConnectedSystemId = systemId,
                SyncRuleId = syncRuleId,
                ConnectedSystemRunProfileId = runProfileId
            };
            seedCtx.Activities.Add(activity);
            await seedCtx.SaveChangesAsync();
            activityId = activity.Id;
        }

        // The worker shape: the Activity is tracked on the SAME context the deletion runs on (JIM.Worker's
        // context tracks by default; this fixture's default is NoTracking, so opt in explicitly).
        await using var workerCtx = NewContext();
        var repository = new PostgresDataRepository(workerCtx);
        var trackedActivity = await workerCtx.Activities.AsTracking().SingleAsync(a => a.Id == activityId);

        await repository.ConnectedSystems.DeleteConnectedSystemAsync(systemId);

        // The completion write that follows the deletion on the worker's dispatch boundary.
        trackedActivity.Status = ActivityStatus.Complete;
        trackedActivity.Message = "Deprovisioned.";
        Assert.That(async () => await repository.Activity.UpdateActivityAsync(trackedActivity),
            Throws.Nothing,
            "completing the task's Activity after the deletion must not re-assert the severed foreign keys " +
            "from the tracked instance; the deletion's raw SQL nulled them in the database only");

        await using var assertCtx = NewContext();
        var persisted = await assertCtx.Activities.SingleAsync(a => a.Id == activityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.Status, Is.EqualTo(ActivityStatus.Complete), "the completion itself landed");
            Assert.That(persisted.ConnectedSystemId, Is.Null, "the Connected System reference stays severed");
            Assert.That(persisted.SyncRuleId, Is.Null, "the Synchronisation Rule reference stays severed");
            Assert.That(persisted.ConnectedSystemRunProfileId, Is.Null, "the Run Profile reference stays severed");
        }
    }

    /// <summary>
    /// An Object Matching Rule can be orphaned of both parents: EF Core nulls the optional owner foreign key when
    /// a rule is removed from its owner's collection rather than deleted, which is what the Synchronisation Rule
    /// save path's clears did before #1589. The deletion sequence removes a system's matching rules by scope, so
    /// an orphan matched neither arm, its source's reference to a Connected System attribute refused the attribute
    /// delete with 23503, and the whole deletion rolled back: the system could never be deleted. The sweep must
    /// remove orphans reaching the system through their sources, because existing deployments may already hold them.
    /// </summary>
    [Test]
    public async Task DeleteConnectedSystemAsync_WithAnOrphanedObjectMatchingRule_DeletesItAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (systemId, _) = await SeedSystemAsync(suffix);

        int orphanedRuleId;
        await using (var ctx = NewContext())
        {
            var objectType = new ConnectedSystemObjectType { ConnectedSystemId = systemId, Name = "user", Selected = true };
            ctx.ConnectedSystemObjectTypes.Add(objectType);
            await ctx.SaveChangesAsync();

            var attribute = new ConnectedSystemObjectTypeAttribute { ConnectedSystemObjectType = objectType, Name = "employeeId" };
            ctx.ConnectedSystemAttributes.Add(attribute);
            await ctx.SaveChangesAsync();

            // The orphan: no owning Object Type, no owning Synchronisation Rule, one source still referencing
            // the system's attribute. Exactly what a pre-fix save-path clear left behind.
            var orphanedRule = new ObjectMatchingRule
            {
                Order = 0,
                Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = attribute.Id }]
            };
            ctx.ObjectMatchingRules.Add(orphanedRule);
            await ctx.SaveChangesAsync();
            orphanedRuleId = orphanedRule.Id;
        }

        await using (var deleteCtx = NewContext())
        {
            var repository = new PostgresDataRepository(deleteCtx);

            Assert.That(async () => await repository.ConnectedSystems.DeleteConnectedSystemAsync(systemId),
                Throws.Nothing,
                "an orphaned Object Matching Rule must not make a Connected System undeletable; its source's " +
                "attribute reference has to be swept before the attributes are deleted");
        }

        await using var assertCtx = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await assertCtx.ConnectedSystems.AnyAsync(cs => cs.Id == systemId), Is.False,
                "the Connected System itself is gone");
            Assert.That(await assertCtx.ObjectMatchingRules.AnyAsync(r => r.Id == orphanedRuleId), Is.False,
                "the orphan goes with it rather than being left behind");
        }
    }
}
