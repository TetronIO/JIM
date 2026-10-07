// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL cover for deleting a Synchronisation Rule that history still names (#1990).
/// <para>
/// Every Metaverse Object change a synchronisation records names the rule that caused it, so any rule that has done
/// any work is referenced from <c>MetaverseObjectChanges</c>. That foreign key was <c>NO ACTION</c>, so the delete was
/// refused with <c>23503</c> and such a rule could never be deleted. The in-memory provider enforces no foreign keys,
/// so only a real database can show it.
/// </para>
/// <para>
/// The schema-level counterpart is the Synchronisation Rule surface in
/// <see cref="DeletePathForeignKeyCoverageTests"/>, which fails for any history table added later that refers to a
/// rule without clearing the reference on delete. Opt-in via <c>JIM_TEST_RESET_DB</c>; ignored when it is absent.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleDeletionDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Synchronisation Rule deletion tests.");

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
    public async Task DeleteSyncRuleAsync_RuleNamedByMetaverseObjectChange_DeletesRuleAndKeepsChangeWithNullSyncRuleIdAsync()
    {
        var seed = await SeedRuleNamedByHistoryAsync();

        // Delete the way the portal and the REST API do: load and delete on one short-lived NoTracking unit of work.
        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = await jim.ConnectedSystems.GetSyncRuleAsync(seed.RuleId);
            var initiator = await ctx.MetaverseObjects.SingleAsync(x => x.Id == seed.InitiatorId);
            var deletion = await jim.ConnectedSystems.DeleteSyncRuleAsync(rule!, initiator);
            Assert.That(deletion.RecallQueued, Is.False, "precondition: the rule contributes no values, so it is deleted synchronously");
        }

        await using var verify = NewContext();
        var change = await verify.MetaverseObjectChanges.SingleOrDefaultAsync(c => c.Id == seed.ChangeId);
        var activity = await verify.Activities.SingleOrDefaultAsync(a => a.Id == seed.ActivityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.SyncRules.AnyAsync(r => r.Id == seed.RuleId), Is.False, "the rule must be deleted");
            Assert.That(change, Is.Not.Null, "the change history must survive the rule's deletion");
            Assert.That(change!.SyncRuleId, Is.Null, "the change must no longer reference the deleted rule");
            Assert.That(change.SyncRuleName, Is.EqualTo(seed.RuleName), "the snapshot of the rule's name keeps the change readable");
            Assert.That(activity, Is.Not.Null, "the rule's Activity history must survive its deletion");
            Assert.That(activity!.SyncRuleId, Is.Null, "the Activity must no longer reference the deleted rule");
        }
    }

    /// <summary>
    /// The database's own guarantee, independent of any statement the repository runs first: removing a rule row
    /// directly clears every history reference to it rather than refusing.
    /// </summary>
    [Test]
    public async Task DeletingSyncRuleRow_RuleNamedByHistory_ClearsTheReferencesAsync()
    {
        var seed = await SeedRuleNamedByHistoryAsync();

        await using (var ctx = NewContext())
        {
            var deleted = await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""SyncRules"" WHERE ""Id"" = {0}", seed.RuleId);
            Assert.That(deleted, Is.EqualTo(1));
        }

        await using var verify = NewContext();
        var change = await verify.MetaverseObjectChanges.SingleAsync(c => c.Id == seed.ChangeId);
        var activity = await verify.Activities.SingleAsync(a => a.Id == seed.ActivityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.SyncRuleId, Is.Null);
            Assert.That(activity.SyncRuleId, Is.Null);
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Seeding
    // -----------------------------------------------------------------------------------------------------------------

    private sealed record HistorySeed(int RuleId, string RuleName, Guid InitiatorId, Guid ChangeId, Guid ActivityId);

    /// <summary>
    /// Seeds an import rule created through the server (so its creation Activity names it, as in production), and a
    /// Metaverse Object with one change record attributed to the rule, as a synchronisation writes it.
    /// </summary>
    private async Task<HistorySeed> SeedRuleNamedByHistoryAsync()
    {
        int systemId, csTypeId, mvTypeId;
        Guid initiatorId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
            var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
            var initiator = new MetaverseObject { Type = mvType, CachedDisplayName = "Test Administrator" };

            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemObjectTypes.Add(csType);
            seed.MetaverseObjectTypes.Add(mvType);
            seed.MetaverseObjects.Add(initiator);
            await seed.SaveChangesAsync();

            (systemId, csTypeId, mvTypeId, initiatorId) = (system.Id, csType.Id, mvType.Id, initiator.Id);
        }

        const string ruleName = "HR Import";
        SyncRule rule;
        await using (var ctx = NewContext())
        {
            // Navigations loaded detached, as the portal editor binds them; the create path resolves their ids.
            rule = new SyncRule
            {
                Name = ruleName,
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystem = await ctx.ConnectedSystems.SingleAsync(x => x.Id == systemId),
                ConnectedSystemObjectType = await ctx.ConnectedSystemObjectTypes.SingleAsync(x => x.Id == csTypeId),
                MetaverseObjectType = await ctx.MetaverseObjectTypes.SingleAsync(x => x.Id == mvTypeId)
            };
        }
        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var initiator = await ctx.MetaverseObjects.SingleAsync(x => x.Id == initiatorId);
            Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator), Is.True, "Failed to create the rule.");
        }

        await using var history = NewContext();
        var activityId = await history.Activities.Where(a => a.SyncRuleId == rule.Id).Select(a => a.Id).FirstOrDefaultAsync();
        Assert.That(activityId, Is.Not.EqualTo(Guid.Empty), "precondition: the rule's creation Activity names it");

        var personType = await history.MetaverseObjectTypes.AsTracking().SingleAsync(t => t.Id == mvTypeId);
        var person = new MetaverseObject { Type = personType, CachedDisplayName = "Jane Smith" };
        history.MetaverseObjects.Add(person);
        var change = new MetaverseObjectChange
        {
            MetaverseObject = person,
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Projected,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule,
            SyncRuleId = rule.Id,
            SyncRuleName = ruleName
        };
        history.MetaverseObjectChanges.Add(change);
        await history.SaveChangesAsync();

        return new HistorySeed(rule.Id, ruleName, initiatorId, change.Id, activityId);
    }

    // The sync repository is passed explicitly, as every host passes it: deleting a Synchronisation Rule checks the
    // export queue for changes the deletion left without authority, which reads through it.
    private static JimApplication NewJimApplication(JimDbContext context)
    {
        var repository = new PostgresDataRepository(context);
        return new JimApplication(repository, syncRepository: repository.Sync);
    }
}
