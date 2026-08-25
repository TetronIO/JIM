// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Pins the Data Generation Template create and update repository paths (#894) against real PostgreSQL: a detached
/// template graph referencing persisted Metaverse Object Types, Metaverse Attributes and Example Data Sets must insert
/// only the template's own rows, a graph-replacing update must physically delete the superseded child rows while
/// leaving the referenced entities untouched, and a scalar-only update must rename without disturbing any child row.
/// </summary>
/// <remarks>
/// <para>
/// Real PostgreSQL and a no-tracking context, matching JIM.Web (which is what the REST API and PowerShell surfaces run
/// on). The EF in-memory provider tracks by default, resolves identities and enforces no key constraints, so it would
/// mask every fault this fixture exists to catch: a re-inserted Metaverse Object Type, a silently no-op update against
/// a detached graph, and orphaned template attribute rows.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ExampleDataTemplateCrudDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Data Generation Template CRUD tests.");

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

    [Test]
    public async Task CreateTemplateAsync_DetachedGraphReferencingPersistedEntities_InsertsOnlyTemplateRowsAsync()
    {
        await SeedReferenceDataAsync();

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var template = await BuildTemplateGraphAsync(ctx, "Users", ["Display Name", "Job Title"]);
            Assert.That(async () => await repository.ExampleData.CreateTemplateAsync(template), Throws.Nothing,
                "A detached graph referencing persisted entities must insert without trying to re-insert them.");
        }

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ExampleDataTemplates.CountAsync(), Is.EqualTo(1));
            Assert.That(await verify.ExampleDataObjectTypes.CountAsync(), Is.EqualTo(1));
            Assert.That(await verify.ExampleDataTemplateAttributes.CountAsync(), Is.EqualTo(2));
            Assert.That(await verify.MetaverseObjectTypes.CountAsync(), Is.EqualTo(1), "The referenced Metaverse Object Type must not be duplicated.");
            Assert.That(await verify.MetaverseAttributes.CountAsync(), Is.EqualTo(2), "The referenced Metaverse Attributes must not be duplicated.");
            Assert.That(await verify.ExampleDataSets.CountAsync(), Is.EqualTo(1), "The referenced Example Data Set must not be duplicated.");
            Assert.That(await verify.ExampleDataSetValues.CountAsync(), Is.EqualTo(2), "The referenced Example Data Set's values must not be duplicated.");
        }
    }

    [Test]
    public async Task UpdateTemplateAsync_ReplacingObjectTypes_ReplacesTheGraphAndLeavesReferencedEntitiesIntactAsync()
    {
        await SeedReferenceDataAsync();
        await using (var seed = NewContext())
        {
            var repository = new PostgresDataRepository(seed);
            await repository.ExampleData.CreateTemplateAsync(await BuildTemplateGraphAsync(seed, "Users", ["Display Name", "Job Title"]));
        }

        int templateId;
        List<int> originalAttributeIds;
        await using (var read = NewContext())
        {
            var persisted = await read.ExampleDataTemplates.Include(t => t.ObjectTypes).ThenInclude(ot => ot.TemplateAttributes).SingleAsync();
            templateId = persisted.Id;
            originalAttributeIds = persisted.ObjectTypes.SelectMany(ot => ot.TemplateAttributes).Select(ta => ta.Id).ToList();
        }
        Assert.That(originalAttributeIds, Has.Count.EqualTo(2));

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            // the shape the REST/PowerShell surfaces submit: a freshly-built graph carrying the template's id, whose
            // referenced entities were loaded no-tracking (Metaverse Object Type included with its Attributes, as the
            // portal and API load it) and so arrive detached.
            var replacement = await BuildTemplateGraphAsync(ctx, "Users", ["Job Title"]);
            replacement.Id = templateId;
            replacement.Name = "Users (revised)";
            replacement.LastUpdated = new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc);
            replacement.LastUpdatedByType = ActivityInitiatorType.User;
            replacement.LastUpdatedByName = "Admin User";

            Assert.That(async () => await repository.ExampleData.UpdateTemplateAsync(replacement, replaceObjectTypes: true), Throws.Nothing);
        }

        await using var verify = NewContext();
        var updated = await verify.ExampleDataTemplates.Include(t => t.ObjectTypes).ThenInclude(ot => ot.TemplateAttributes).SingleAsync();
        var newAttributeIds = updated.ObjectTypes.SelectMany(ot => ot.TemplateAttributes).Select(ta => ta.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.Name, Is.EqualTo("Users (revised)"));
            Assert.That(updated.LastUpdatedByName, Is.EqualTo("Admin User"), "Audit fields must be copied onto the persisted template.");
            Assert.That(updated.ObjectTypes, Has.Count.EqualTo(1));
            Assert.That(newAttributeIds, Has.Count.EqualTo(1), "The replaced graph must carry only the submitted attribute.");
            Assert.That(await verify.ExampleDataTemplateAttributes.CountAsync(), Is.EqualTo(1),
                "The superseded attribute rows must be physically deleted, not orphaned.");
            Assert.That(await verify.ExampleDataObjectTypes.CountAsync(), Is.EqualTo(1),
                "The superseded Object Type row must be physically deleted, not orphaned.");
            Assert.That(newAttributeIds.Intersect(originalAttributeIds), Is.Empty, "The superseded attribute rows must be gone.");
            Assert.That(await verify.MetaverseObjectTypes.CountAsync(), Is.EqualTo(1), "The referenced Metaverse Object Type must be untouched.");
            Assert.That(await verify.MetaverseAttributes.CountAsync(), Is.EqualTo(2), "The referenced Metaverse Attributes must be untouched.");
            Assert.That(await verify.ExampleDataSets.CountAsync(), Is.EqualTo(1), "The referenced Example Data Set must be untouched.");
            Assert.That(await verify.ExampleDataSetValues.CountAsync(), Is.EqualTo(2), "The referenced Example Data Set's values must be untouched.");
        }
    }

    [Test]
    public async Task UpdateTemplateAsync_ScalarOnly_RenamesWithoutTouchingChildRowsAsync()
    {
        await SeedReferenceDataAsync();
        await using (var seed = NewContext())
        {
            var repository = new PostgresDataRepository(seed);
            await repository.ExampleData.CreateTemplateAsync(await BuildTemplateGraphAsync(seed, "Users", ["Display Name", "Job Title"]));
        }

        int templateId;
        List<int> originalAttributeIds;
        await using (var read = NewContext())
        {
            var persisted = await read.ExampleDataTemplates.Include(t => t.ObjectTypes).ThenInclude(ot => ot.TemplateAttributes).SingleAsync();
            templateId = persisted.Id;
            originalAttributeIds = persisted.ObjectTypes.SelectMany(ot => ot.TemplateAttributes).Select(ta => ta.Id).OrderBy(id => id).ToList();
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            // a rename carries no Object Types at all; the persisted graph must survive it.
            var rename = new ExampleDataTemplate { Id = templateId, Name = "Users (renamed)", LastUpdatedByName = "Admin User" };
            await repository.ExampleData.UpdateTemplateAsync(rename, replaceObjectTypes: false);
        }

        await using var verify = NewContext();
        var updated = await verify.ExampleDataTemplates.Include(t => t.ObjectTypes).ThenInclude(ot => ot.TemplateAttributes).SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated.Name, Is.EqualTo("Users (renamed)"), "The rename must actually persist, not silently no-op against a detached graph.");
            Assert.That(updated.LastUpdatedByName, Is.EqualTo("Admin User"));
            Assert.That(updated.ObjectTypes, Has.Count.EqualTo(1), "A scalar-only update must leave the Object Types alone.");
            Assert.That(updated.ObjectTypes.SelectMany(ot => ot.TemplateAttributes).Select(ta => ta.Id).OrderBy(id => id).ToList(),
                Is.EqualTo(originalAttributeIds), "A scalar-only update must not recreate the child rows.");
        }
    }

    [Test]
    public async Task UpdateTemplateAsync_TemplateNotFound_ThrowsAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var orphan = new ExampleDataTemplate { Id = 4242, Name = "Nonexistent" };

        Assert.That(async () => await repository.ExampleData.UpdateTemplateAsync(orphan, replaceObjectTypes: false),
            Throws.InstanceOf<InvalidOperationException>(), "An update against a template that no longer exists must fail fast, not silently do nothing.");
    }

    // -- helpers -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Seeds the persisted entities a Data Generation Template references: one Metaverse Object Type with two
    /// Metaverse Attributes, and one Example Data Set with two values.
    /// </summary>
    private async Task SeedReferenceDataAsync()
    {
        await using var ctx = NewContext();
        var displayName = new MetaverseAttribute { Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var jobTitle = new MetaverseAttribute { Name = "Job Title", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var objectType = new MetaverseObjectType { Name = "User", PluralName = "Users", Attributes = [displayName, jobTitle] };
        ctx.MetaverseObjectTypes.Add(objectType);
        ctx.ExampleDataSets.Add(new ExampleDataSet
        {
            Name = "Job Titles",
            Culture = "en-GB",
            Values = [new ExampleDataSetValue { StringValue = "Engineer" }, new ExampleDataSetValue { StringValue = "Architect" }]
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Builds a detached template graph over no-tracking loads of the seeded entities, which is how the REST API and
    /// PowerShell surfaces assemble a submitted template. The Metaverse Object Type is loaded with its Attributes
    /// (the way the portal and API load it), which is what makes the many-to-many binding a live hazard on save.
    /// </summary>
    private static async Task<ExampleDataTemplate> BuildTemplateGraphAsync(JimDbContext ctx, string templateName, string[] attributeNames)
    {
        var objectType = await ctx.MetaverseObjectTypes.Include(t => t.Attributes).SingleAsync(t => t.Name == "User");
        var exampleDataSet = await ctx.ExampleDataSets.Include(s => s.Values).SingleAsync(s => s.Name == "Job Titles");

        var template = new ExampleDataTemplate { Name = templateName };
        var templateObjectType = new ExampleDataObjectType { MetaverseObjectType = objectType, ObjectsToCreate = 250 };
        foreach (var attributeName in attributeNames)
        {
            templateObjectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
            {
                MetaverseAttribute = objectType.Attributes.Single(a => a.Name == attributeName),
                PopulatedValuesPercentage = 100,
                ExampleDataSetInstances = [new ExampleDataSetInstance { ExampleDataSet = exampleDataSet, Order = 0 }]
            });
        }

        template.ObjectTypes.Add(templateObjectType);
        return template;
    }
}
