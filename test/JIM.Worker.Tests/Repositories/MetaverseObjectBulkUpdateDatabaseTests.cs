// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the synchronisation Metaverse Object update path
/// (<c>SyncRepository.UpdateMetaverseObjectsAsync</c>).
///
/// Reproduces the pre-release Full Regression failure (Scenario14-AttributePriority, activity
/// 019f4501-...): a Metaverse Object bulk-created via the raw COPY path is attached to the change
/// tracker without its real <c>xmin</c> (the concurrency token defaults to 0), so the next EF
/// <c>SaveChangesAsync</c> update of that object in the same flush issues <c>... WHERE xmin = 0</c>,
/// matches 0 rows, and throws <see cref="DbUpdateConcurrencyException"/> - an unhandled failure that
/// aborts the whole sync run. This is exactly the hazard the design note in
/// <c>SyncRepository.MvoOperations.cs</c> predicted. The EF Core in-memory provider does not enforce
/// xmin optimistic concurrency, so this can only be verified against a real database.
///
/// Opt-in via <c>JIM_TEST_RESET_DB</c>; ignored when it is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseObjectBulkUpdateDatabaseTests
{
    private string _connectionString = null!;

    // Tracking is left at the default (TrackAll): the failure depends on the freshly-created MVO
    // remaining tracked with xmin = 0 between the create and the update, which is how the sync engine
    // reuses a single context across a page flush.
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Metaverse Object bulk-update tests.");

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
    /// Seeds a Person Metaverse Object Type with three single-valued text attributes and returns their ids.
    /// </summary>
    private async Task<(int PersonTypeId, int DisplayNameId, int DepartmentId, int JobTitleId)> SeedTypeAsync()
    {
        await using var seed = NewContext();

        var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
        var displayName = new MetaverseAttribute { Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        var department = new MetaverseAttribute { Name = "Department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        var jobTitle = new MetaverseAttribute { Name = "Job Title", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        personType.Attributes.Add(displayName);
        personType.Attributes.Add(department);
        personType.Attributes.Add(jobTitle);

        seed.MetaverseObjectTypes.Add(personType);
        await seed.SaveChangesAsync();

        return (personType.Id, displayName.Id, department.Id, jobTitle.Id);
    }

    private static MetaverseObjectAttributeValue TextValue(int attributeId, string value) =>
        new() { AttributeId = attributeId, StringValue = value, NullValue = false };

    /// <summary>
    /// The core regression: after a raw bulk-create, updating the same Metaverse Object in the same
    /// context must persist the change rather than throw <see cref="DbUpdateConcurrencyException"/>.
    /// </summary>
    [Test]
    public async Task UpdateMetaverseObjectsAsync_AfterBulkCreateInSameContext_PersistsChangeAsync()
    {
        var ids = await SeedTypeAsync();

        await using var ctx = NewContext();
        var repo = new PostgresDataRepository(ctx);
        var personType = await ctx.MetaverseObjectTypes.FindAsync(ids.PersonTypeId);

        var mvo = new MetaverseObject
        {
            Type = personType!,
            AttributeValues = { TextValue(ids.DepartmentId, "Sales") }
        };

        // Raw COPY/INSERT create - attaches the MVO to the tracker with xmin defaulted to 0.
        await repo.Sync.CreateMetaverseObjectsAsync(new[] { mvo });

        // Attribute Flow adds a new value; the sync engine now queues the MVO for update.
        mvo.AttributeValues.Add(TextValue(ids.DisplayNameId, "Alice Example"));

        // Must not throw: on the pre-fix EF path this throws DbUpdateConcurrencyException (WHERE xmin = 0).
        await repo.Sync.UpdateMetaverseObjectsAsync(new[] { mvo });

        // Verify the change round-tripped through PostgreSQL.
        await using var verifyCtx = NewContext();
        var persisted = await verifyCtx.MetaverseObjects
            .Include(o => o.AttributeValues)
            .SingleAsync(o => o.Id == mvo.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.AttributeValues, Has.Count.EqualTo(2), "both attribute values should be persisted");
            Assert.That(persisted.AttributeValues.Any(av => av.AttributeId == ids.DepartmentId && av.StringValue == "Sales"), Is.True, "original Department value");
            Assert.That(persisted.AttributeValues.Any(av => av.AttributeId == ids.DisplayNameId && av.StringValue == "Alice Example"), Is.True, "added Display Name value");
        }
    }

    /// <summary>
    /// The update path must apply the full attribute-value delta: insert the added value and delete the
    /// removed one (the sync engine models a change of value as a remove + add), leaving unchanged values intact.
    /// </summary>
    [Test]
    public async Task UpdateMetaverseObjectsAsync_AppliesAttributeValueAddAndRemoveAsync()
    {
        var ids = await SeedTypeAsync();

        await using var ctx = NewContext();
        var repo = new PostgresDataRepository(ctx);
        var personType = await ctx.MetaverseObjectTypes.FindAsync(ids.PersonTypeId);

        var displayNameValue = TextValue(ids.DisplayNameId, "Alice Example");
        var departmentValue = TextValue(ids.DepartmentId, "Sales");
        var mvo = new MetaverseObject
        {
            Type = personType!,
            AttributeValues = { displayNameValue, departmentValue }
        };

        await repo.Sync.CreateMetaverseObjectsAsync(new[] { mvo });

        // Remove Department, add Job Title, leave Display Name unchanged.
        mvo.AttributeValues.Remove(departmentValue);
        mvo.AttributeValues.Add(TextValue(ids.JobTitleId, "Engineer"));

        await repo.Sync.UpdateMetaverseObjectsAsync(new[] { mvo });

        await using var verifyCtx = NewContext();
        var persisted = await verifyCtx.MetaverseObjects
            .Include(o => o.AttributeValues)
            .SingleAsync(o => o.Id == mvo.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.AttributeValues, Has.Count.EqualTo(2), "one value removed, one added, one unchanged");
            Assert.That(persisted.AttributeValues.Any(av => av.AttributeId == ids.DisplayNameId && av.StringValue == "Alice Example"), Is.True, "unchanged Display Name retained");
            Assert.That(persisted.AttributeValues.Any(av => av.AttributeId == ids.JobTitleId && av.StringValue == "Engineer"), Is.True, "added Job Title persisted");
            Assert.That(persisted.AttributeValues.Any(av => av.AttributeId == ids.DepartmentId), Is.False, "removed Department deleted");
        }
    }

    /// <summary>
    /// The #119 deletion-trigger marker columns (DeletionTriggeredBySystemId/Name and the decision-time
    /// DeletionPolicySnapshotJson) travel through the same raw bulk create and update paths as the existing
    /// deletion markers (LastConnectorDisconnectedDate, DeletionInitiatedBy*). The in-memory provider stores
    /// the object graph verbatim, so only a real-PostgreSQL round trip proves the hand-written column lists
    /// and writers persist them.
    /// </summary>
    [Test]
    public async Task UpdateMetaverseObjectsAsync_DeletionTriggerMarkerColumns_RoundTripThroughBulkPathsAsync()
    {
        var ids = await SeedTypeAsync();

        await using var ctx = NewContext();
        var repo = new PostgresDataRepository(ctx);
        var personType = await ctx.MetaverseObjectTypes.FindAsync(ids.PersonTypeId);

        var snapshotJson = """{"deletionRule":"WhenAuthoritativeSourceDisconnected","triggerMode":"SpecificSourcesDisconnect"}""";
        var mvo = new MetaverseObject
        {
            Type = personType!,
            AttributeValues = { TextValue(ids.DepartmentId, "Sales") },
            DeletionTriggeredBySystemId = 7,
            DeletionTriggeredBySystemName = "HR System",
            DeletionPolicySnapshotJson = snapshotJson
        };

        // Raw COPY/INSERT create must persist the marker columns.
        await repo.Sync.CreateMetaverseObjectsAsync(new[] { mvo });

        await using (var verifyCreateCtx = NewContext())
        {
            var created = await verifyCreateCtx.MetaverseObjects.AsNoTracking().SingleAsync(o => o.Id == mvo.Id);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(created.DeletionTriggeredBySystemId, Is.EqualTo(7), "bulk create must persist DeletionTriggeredBySystemId");
                Assert.That(created.DeletionTriggeredBySystemName, Is.EqualTo("HR System"), "bulk create must persist DeletionTriggeredBySystemName");
                Assert.That(created.DeletionPolicySnapshotJson, Is.EqualTo(snapshotJson), "bulk create must persist DeletionPolicySnapshotJson");
            }
        }

        // A rejoin cancels the scheduled deletion: the markers are cleared together, and the raw bulk update
        // must persist the cleared state (a dropped column here would silently resurrect the stale trigger).
        mvo.DeletionTriggeredBySystemId = null;
        mvo.DeletionTriggeredBySystemName = null;
        mvo.DeletionPolicySnapshotJson = null;
        await repo.Sync.UpdateMetaverseObjectsAsync(new[] { mvo });

        await using var verifyUpdateCtx = NewContext();
        var updated = await verifyUpdateCtx.MetaverseObjects.AsNoTracking().SingleAsync(o => o.Id == mvo.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.DeletionTriggeredBySystemId, Is.Null, "bulk update must persist a cleared DeletionTriggeredBySystemId");
            Assert.That(updated.DeletionTriggeredBySystemName, Is.Null, "bulk update must persist a cleared DeletionTriggeredBySystemName");
            Assert.That(updated.DeletionPolicySnapshotJson, Is.Null, "bulk update must persist a cleared DeletionPolicySnapshotJson");
        }
    }

    /// <summary>
    /// Seeds a Connected System and two import Synchronisation Rules, so attribute values can carry real provenance
    /// foreign keys.
    /// </summary>
    private async Task<(int SystemId, int FirstRuleId, int SecondRuleId)> SeedSystemAndRulesAsync(int metaverseObjectTypeId)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "HR System", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();

        SyncRule NewRule(string name) => new()
        {
            Name = name,
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id,
            MetaverseObjectTypeId = metaverseObjectTypeId
        };

        var firstRule = NewRule("Import from HR");
        var secondRule = NewRule("Import from Contractors");
        seed.SyncRules.AddRange(firstRule, secondRule);
        await seed.SaveChangesAsync();

        return (system.Id, firstRule.Id, secondRule.Id);
    }

    /// <summary>
    /// The provenance of a surviving attribute value (#1292) is the one thing the synchronisation engine changes on a
    /// row it does not replace: a winning contribution that supplies the value the Metaverse Object already holds
    /// stages neither an addition nor a removal, and instead takes the row's contributing rule and system over. The
    /// update path reconciled attribute values purely by id, so that mutation was silently dropped; only a real
    /// round trip proves it is written, because the in-memory provider stores the mutated object graph verbatim.
    /// </summary>
    [Test]
    public async Task UpdateMetaverseObjectsAsync_ProvenanceOfSurvivingAttributeValue_IsPersistedAsync()
    {
        var ids = await SeedTypeAsync();
        var rules = await SeedSystemAndRulesAsync(ids.PersonTypeId);

        await using var ctx = NewContext();
        var repo = new PostgresDataRepository(ctx);
        var personType = await ctx.MetaverseObjectTypes.FindAsync(ids.PersonTypeId);

        var departmentValue = TextValue(ids.DepartmentId, "Sales");
        departmentValue.ContributedBySystemId = rules.SystemId;
        departmentValue.ContributedBySyncRuleId = rules.SecondRuleId;

        var mvo = new MetaverseObject
        {
            Type = personType!,
            AttributeValues = { departmentValue, TextValue(ids.DisplayNameId, "Alice Example") }
        };

        await repo.Sync.CreateMetaverseObjectsAsync(new[] { mvo });

        // The higher-priority rule contributes the identical value and takes the row over; the value itself is untouched.
        departmentValue.ContributedBySyncRuleId = rules.FirstRuleId;

        await repo.Sync.UpdateMetaverseObjectsAsync(new[] { mvo });

        await using var verifyCtx = NewContext();
        var persisted = await verifyCtx.MetaverseObjects
            .AsNoTracking()
            .Include(o => o.AttributeValues)
            .SingleAsync(o => o.Id == mvo.Id);

        var department = persisted.AttributeValues.Single(av => av.AttributeId == ids.DepartmentId);
        var displayName = persisted.AttributeValues.Single(av => av.AttributeId == ids.DisplayNameId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(department.ContributedBySyncRuleId, Is.EqualTo(rules.FirstRuleId), "the taken-over provenance must reach the database");
            Assert.That(department.ContributedBySystemId, Is.EqualTo(rules.SystemId), "the contributing system is unchanged");
            Assert.That(department.StringValue, Is.EqualTo("Sales"), "the value itself is untouched by a provenance takeover");
            Assert.That(department.Id, Is.EqualTo(departmentValue.Id), "the row is updated in place, not churned");
            Assert.That(displayName.ContributedBySyncRuleId, Is.Null, "a value nobody took over keeps its provenance");
        }
    }
}
