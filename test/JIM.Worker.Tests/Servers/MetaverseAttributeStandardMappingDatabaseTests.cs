// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the custom-attribute Standard Mappings update (issue #1104 Phase 2 UI). The
/// mocked unit tests cannot prove that GetMetaverseAttributeWithObjectTypesAsync eager-loads the mappings, that the
/// tracked reconcile persists adds, note updates and removals, or that retained mappings genuinely keep their rows.
/// Opt-in via <c>JIM_TEST_RESET_DB</c>; ignored when it is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseAttributeStandardMappingDatabaseTests
{
    private string _connectionString = null!;

    // Mirrors production hosts: JimDbContext defaults to NoTracking, so the update path's AsTracking opt-in is
    // what makes the reconcile persist.
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Standard Mapping update tests.");

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
    public async Task UpdateMetaverseAttributeStandardMappingsAsync_ReconcilesAddsUpdatesAndRemovalsAcrossContextsAsync()
    {
        // seed a custom attribute with one mapping to be removed and one to be retained (with a note change)
        int attributeId;
        await using (var seed = NewContext())
        {
            var attribute = new MetaverseAttribute { Name = "Cost Centre", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            attribute.StandardMappings.Add(new MetaverseAttributeStandardMapping { Standard = AttributeStandard.Ldap, CounterpartName = "obsolete" });
            attribute.StandardMappings.Add(new MetaverseAttributeStandardMapping { Standard = AttributeStandard.Scim, CounterpartName = "costCenter" });
            seed.MetaverseAttributes.Add(attribute);
            await seed.SaveChangesAsync();
            attributeId = attribute.Id;
        }

        int retainedMappingId;
        await using (var check = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(check));
            var loaded = await jim.Metaverse.GetMetaverseAttributeWithObjectTypesAsync(attributeId);
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.StandardMappings, Has.Count.EqualTo(2), "the retrieval must eager-load the Standard Mappings");
            retainedMappingId = loaded!.StandardMappings.Single(m => m.CounterpartName == "costCenter").Id;
        }

        await using (var update = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(update));
            var requested = new List<MetaverseAttributeStandardMapping>
            {
                new() { Standard = AttributeStandard.Scim, CounterpartName = "costCenter", Notes = "SCIM Enterprise User extension." },
                new() { Standard = AttributeStandard.Jim, CounterpartName = "Cost Centre" }
            };
            await jim.Metaverse.UpdateMetaverseAttributeStandardMappingsAsync(attributeId, requested,
                new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = "Test Admin" }, "align with finance system");
        }

        await using (var verify = NewContext())
        {
            var rows = await verify.MetaverseAttributeStandardMappings
                .Where(m => m.MetaverseAttributeId == attributeId)
                .OrderBy(m => m.Standard)
                .ToListAsync();
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows.Any(m => m.CounterpartName == "obsolete"), Is.False, "the removed mapping's row must be deleted");
            var retained = rows.Single(m => m.Standard == AttributeStandard.Scim);
            Assert.That(retained.Id, Is.EqualTo(retainedMappingId), "the retained mapping must keep its row");
            Assert.That(retained.Notes, Is.EqualTo("SCIM Enterprise User extension."));
            Assert.That(rows.Any(m => m.Standard == AttributeStandard.Jim && m.CounterpartName == "Cost Centre"), Is.True);
        }
    }
}