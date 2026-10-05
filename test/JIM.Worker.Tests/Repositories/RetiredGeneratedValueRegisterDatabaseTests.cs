// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for the retired values register (Unique Value Generation, #242, Phase 6): the
/// case-insensitive unique index, every raw-SQL write that retires a value (Metaverse Object deletion through both
/// repositories, Connected System Object deletion and a connector space clear, supersession, and the database
/// trigger that retires a removed flow's values), the retired gate's lookup, the purge "Start again" performs, and
/// the paged read. Each write is round-tripped field by field, per src/CLAUDE.md "Raw SQL Column Lists" rule 3: the
/// column-completeness guard cannot catch a writer projecting values in the wrong order, and the in-memory provider
/// cannot run the SQL at all.
/// <para>
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables, as every <c>RequiresPostgres</c> fixture is.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class RetiredGeneratedValueRegisterDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL retired values register tests.");

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
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    private static SyncRepository NewSyncRepository(JimDbContext ctx) => new(new PostgresDataRepository(ctx));

    // ---- Schema ----

    [Test]
    public async Task UniqueIndex_SameAttributeSameValueDifferingOnlyInCase_IsRejectedAsync()
    {
        var estate = await SeedEstateAsync();
        await using var ctx = NewContext();
        ctx.RetiredGeneratedValues.Add(Entry(estate.MvTextAttributeId, "Joe.Bloggs"));
        await ctx.SaveChangesAsync();

        await using var ctx2 = NewContext();
        ctx2.RetiredGeneratedValues.Add(Entry(estate.MvTextAttributeId, "JOE.BLOGGS"));

        var ex = Assert.ThrowsAsync<DbUpdateException>(() => ctx2.SaveChangesAsync());
        Assert.That(ex!.InnerException, Is.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo("23505"));
    }

    [Test]
    public async Task UniqueIndex_SameValueOnAnotherAttribute_IsAllowedAsync()
    {
        var estate = await SeedEstateAsync();
        await using var ctx = NewContext();
        ctx.RetiredGeneratedValues.Add(Entry(estate.MvTextAttributeId, "joe.bloggs"));
        ctx.RetiredGeneratedValues.Add(Entry(estate.MvSecondTextAttributeId, "joe.bloggs"));

        Assert.That(() => ctx.SaveChangesAsync(), Throws.Nothing);
    }

    [Test]
    public async Task Attribute_Deleted_TakesItsRegisterWithItAsync()
    {
        var estate = await SeedEstateAsync();
        await using (var ctx = NewContext())
        {
            ctx.RetiredGeneratedValues.Add(Entry(estate.MvSecondTextAttributeId, "gone.with.it"));
            await ctx.SaveChangesAsync();
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""MetaverseAttributes"" WHERE ""Id"" = {0}", estate.MvSecondTextAttributeId);
        }

        await using var check = NewContext();
        Assert.That(await check.RetiredGeneratedValues.AnyAsync(r => r.MetaverseAttributeId == estate.MvSecondTextAttributeId), Is.False);
    }

    // ---- Metaverse Object deletion ----

    [Test]
    public async Task SyncRepositoryDeleteMetaverseObjectsAsync_NeverReuseOn_RetiresTheHeldValueAndReportsItAsync()
    {
        var estate = await SeedEstateAsync(neverReuse: true);
        await AddImportAssignmentAsync(estate, "Joe.Bloggs");

        IReadOnlyList<GeneratedValueRetirement> reported;
        await using (var ctx = NewContext())
        {
            var mvo = await ctx.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == estate.MvoId);
            reported = await NewSyncRepository(ctx).DeleteMetaverseObjectsAsync([mvo]);
        }

        var stored = await SingleRetiredAsync(estate.MvTextAttributeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reported, Has.Count.EqualTo(1));
            Assert.That(reported[0].AttributeName, Is.EqualTo(estate.MvTextAttributeName));
            Assert.That(reported[0].Value, Is.EqualTo("Joe.Bloggs"));
            Assert.That(reported[0].Reason, Is.EqualTo(RetiredGeneratedValueReason.ObjectDeleted));
            Assert.That(reported[0].FromObjectId, Is.EqualTo(estate.MvoId));
            Assert.That(reported[0].MetaverseAttributeId, Is.EqualTo(estate.MvTextAttributeId));
            Assert.That(reported[0].ConnectedSystemObjectTypeAttributeId, Is.Null);

            Assert.That(stored.MetaverseAttributeId, Is.EqualTo(estate.MvTextAttributeId));
            Assert.That(stored.ConnectedSystemObjectTypeAttributeId, Is.Null);
            Assert.That(stored.Value, Is.EqualTo("Joe.Bloggs"));
            Assert.That(stored.NormalisedValue, Is.EqualTo("joe.bloggs"));
            Assert.That(stored.Reason, Is.EqualTo(RetiredGeneratedValueReason.ObjectDeleted));
            Assert.That(stored.FromObjectDisplayName, Is.EqualTo("Joe Bloggs"), "the holder's name is captured before it goes");
            Assert.That(stored.FromObjectId, Is.EqualTo(estate.MvoId));
            Assert.That(stored.ActivityId, Is.Null);
            Assert.That(stored.RetiredAt, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(5)));
        }

        await using var check = NewContext();
        Assert.That(await check.GeneratedValueAssignments.AnyAsync(a => a.MetaverseObjectId == estate.MvoId), Is.False, "the assignment went with its object");
    }

    [Test]
    public async Task SyncRepositoryDeleteMetaverseObjectsAsync_NeverReuseOff_RetiresNothingAsync()
    {
        var estate = await SeedEstateAsync(neverReuse: false);
        await AddImportAssignmentAsync(estate, "joe.bloggs");

        await using (var ctx = NewContext())
        {
            var mvo = await ctx.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == estate.MvoId);
            var reported = await NewSyncRepository(ctx).DeleteMetaverseObjectsAsync([mvo]);
            Assert.That(reported, Is.Empty);
        }

        await using var check = NewContext();
        Assert.That(await check.RetiredGeneratedValues.AnyAsync(r => r.MetaverseAttributeId == estate.MvTextAttributeId), Is.False);
    }

    [Test]
    public async Task SyncRepositoryDeleteMetaverseObjectsAsync_SequenceWithNeverReuseOffStored_StillRetiresAsync()
    {
        var estate = await SeedEstateAsync(neverReuse: false, tokenKind: GeneratedValueTokenKind.Sequence);
        await AddImportAssignmentAsync(estate, "E000123");

        await using (var ctx = NewContext())
        {
            var mvo = await ctx.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == estate.MvoId);
            await NewSyncRepository(ctx).DeleteMetaverseObjectsAsync([mvo]);
        }

        Assert.That((await SingleRetiredAsync(estate.MvTextAttributeId)).Value, Is.EqualTo("E000123"));
    }

    [Test]
    public async Task SyncRepositoryDeleteMetaverseObjectsAsync_ValueAlreadyRetired_KeepsTheFirstEntryAndReportsNothingAsync()
    {
        var estate = await SeedEstateAsync();
        await using (var seed = NewContext())
        {
            var first = Entry(estate.MvTextAttributeId, "joe.bloggs");
            first.Reason = RetiredGeneratedValueReason.Superseded;
            seed.RetiredGeneratedValues.Add(first);
            await seed.SaveChangesAsync();
        }
        await AddImportAssignmentAsync(estate, "Joe.Bloggs");

        await using (var ctx = NewContext())
        {
            var mvo = await ctx.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == estate.MvoId);
            var reported = await NewSyncRepository(ctx).DeleteMetaverseObjectsAsync([mvo]);
            Assert.That(reported, Is.Empty);
        }

        Assert.That((await SingleRetiredAsync(estate.MvTextAttributeId)).Reason, Is.EqualTo(RetiredGeneratedValueReason.Superseded));
    }

    [Test]
    public async Task MetaverseRepositoryDeleteMetaverseObjectAsync_RetiresTheHeldValueAsync()
    {
        var estate = await SeedEstateAsync();
        await AddImportAssignmentAsync(estate, "joe.bloggs");

        await using (var ctx = NewContext())
        {
            var mvo = await ctx.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == estate.MvoId);
            await new PostgresDataRepository(ctx).Metaverse.DeleteMetaverseObjectAsync(mvo);
        }

        var stored = await SingleRetiredAsync(estate.MvTextAttributeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Reason, Is.EqualTo(RetiredGeneratedValueReason.ObjectDeleted));
            Assert.That(stored.FromObjectId, Is.EqualTo(estate.MvoId));
            Assert.That(stored.FromObjectDisplayName, Is.EqualTo("Joe Bloggs"));
        }
    }

    // ---- Connected System Object deletion (export mode) ----

    [Test]
    public async Task SyncRepositoryDeleteConnectedSystemObjectsByIdsAsync_RetiresTheExportValueWithTheAccountsNameAsync()
    {
        var estate = await SeedEstateAsync();
        await AddExportAssignmentAsync(estate, "jbloggs");

        await using (var ctx = NewContext())
            await NewSyncRepository(ctx).DeleteConnectedSystemObjectsByIdsAsync([estate.CsoId]);

        var stored = await SingleRetiredAsync(csAttributeId: estate.CsTextAttributeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.MetaverseAttributeId, Is.Null);
            Assert.That(stored.ConnectedSystemObjectTypeAttributeId, Is.EqualTo(estate.CsTextAttributeId));
            Assert.That(stored.Value, Is.EqualTo("jbloggs"));
            Assert.That(stored.Reason, Is.EqualTo(RetiredGeneratedValueReason.ObjectDeleted));
            Assert.That(stored.FromObjectId, Is.EqualTo(estate.CsoId));
            Assert.That(stored.FromObjectDisplayName, Is.EqualTo("Joe Bloggs (directory)"), "named the way the portal names an account");
        }
    }

    [Test]
    public async Task ConnectorSpaceClear_RetiresTheExportValuesBeforeTheirAttributeValuesGoAsync()
    {
        var estate = await SeedEstateAsync();
        await AddExportAssignmentAsync(estate, "jbloggs");

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.DeleteAllConnectedSystemObjectsAndDependenciesAsync(estate.ConnectedSystemId, deleteChangeHistory: false, recordJoinsForReconciliation: true);

        var stored = await SingleRetiredAsync(csAttributeId: estate.CsTextAttributeId);
        Assert.That(stored.FromObjectDisplayName, Is.EqualTo("Joe Bloggs (directory)"));
    }

    // ---- Supersession ----

    [Test]
    public async Task RetireAndDeleteGeneratedValueAssignmentsAsync_RetiresWithTheActivityAndDeletesTheAssignmentAsync()
    {
        var estate = await SeedEstateAsync();
        var assignmentId = await AddImportAssignmentAsync(estate, "Joe.Bloggs");
        var activityId = Guid.NewGuid();

        IReadOnlyList<GeneratedValueRetirement> reported;
        await using (var ctx = NewContext())
            reported = await NewSyncRepository(ctx).RetireAndDeleteGeneratedValueAssignmentsAsync([assignmentId], RetiredGeneratedValueReason.Superseded, activityId);

        var stored = await SingleRetiredAsync(estate.MvTextAttributeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reported.Single().Value, Is.EqualTo("Joe.Bloggs"));
            Assert.That(stored.Reason, Is.EqualTo(RetiredGeneratedValueReason.Superseded));
            Assert.That(stored.ActivityId, Is.EqualTo(activityId));
            Assert.That(stored.FromObjectId, Is.EqualTo(estate.MvoId));
            Assert.That(stored.FromObjectDisplayName, Is.EqualTo("Joe Bloggs"));
            Assert.That(stored.NormalisedValue, Is.EqualTo("joe.bloggs"));
        }

        await using var check = NewContext();
        Assert.That(await check.GeneratedValueAssignments.AnyAsync(a => a.Id == assignmentId), Is.False);
    }

    [Test]
    public async Task RetireAndDeleteGeneratedValueAssignmentsAsync_NeverReuseOff_DeletesButRetiresNothingAsync()
    {
        var estate = await SeedEstateAsync(neverReuse: false);
        var assignmentId = await AddImportAssignmentAsync(estate, "joe.bloggs");

        await using (var ctx = NewContext())
            Assert.That(await NewSyncRepository(ctx).RetireAndDeleteGeneratedValueAssignmentsAsync([assignmentId], RetiredGeneratedValueReason.Superseded, null), Is.Empty);

        await using var check = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await check.GeneratedValueAssignments.AnyAsync(a => a.Id == assignmentId), Is.False);
            Assert.That(await check.RetiredGeneratedValues.AnyAsync(r => r.MetaverseAttributeId == estate.MvTextAttributeId), Is.False);
        }
    }

    // ---- Flow removal (the trigger) ----

    [Test]
    public async Task DeleteSyncRuleMappingAsync_GeneratedFlowRemoved_TriggerRetiresItsValuesAsRecalledAsync()
    {
        var estate = await SeedEstateAsync();
        await AddImportAssignmentAsync(estate, "Joe.Bloggs");

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.DeleteSyncRuleMappingAsync(new SyncRuleMapping { Id = estate.ImportMappingId });

        var stored = await SingleRetiredAsync(estate.MvTextAttributeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Reason, Is.EqualTo(RetiredGeneratedValueReason.Recalled));
            Assert.That(stored.Value, Is.EqualTo("Joe.Bloggs"));
            Assert.That(stored.NormalisedValue, Is.EqualTo("joe.bloggs"));
            Assert.That(stored.FromObjectId, Is.EqualTo(estate.MvoId));
            Assert.That(stored.FromObjectDisplayName, Is.EqualTo("Joe Bloggs"));
            Assert.That(stored.ActivityId, Is.Null);
            Assert.That(stored.RetiredAt, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(5)));
        }
    }

    [Test]
    public async Task DeleteSyncRule_GeneratedFlowGoesByCascade_TriggerStillRetiresAsync()
    {
        var estate = await SeedEstateAsync();
        await AddImportAssignmentAsync(estate, "joe.bloggs");

        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""SyncRules"" WHERE ""Id"" = {0}", estate.ImportRuleId);

        Assert.That((await SingleRetiredAsync(estate.MvTextAttributeId)).Reason, Is.EqualTo(RetiredGeneratedValueReason.Recalled));
    }

    [Test]
    public async Task GeneratedFlowRemoved_NeverReuseOff_TriggerRetiresNothingAsync()
    {
        var estate = await SeedEstateAsync(neverReuse: false);
        await AddImportAssignmentAsync(estate, "joe.bloggs");

        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""SyncRuleMappingGenerations"" WHERE ""Id"" = {0}", estate.ImportGenerationId);

        await using var check = NewContext();
        Assert.That(await check.RetiredGeneratedValues.AnyAsync(r => r.MetaverseAttributeId == estate.MvTextAttributeId), Is.False);
    }

    [Test]
    public async Task TriggerFunction_NamesEveryRegisterColumnTheRawWritersUseAsync()
    {
        await using var ctx = NewContext();
        var definition = await ctx.Database
            .SqlQueryRaw<string>(@"SELECT pg_get_functiondef('jim_retire_generated_values_on_generation_delete'::regproc) AS ""Value""")
            .SingleAsync();

        var insertList = definition[definition.IndexOf("INSERT INTO", StringComparison.Ordinal)..definition.IndexOf("SELECT", StringComparison.Ordinal)];
        var missing = RetiredGeneratedValueBulkColumns.RetiredGeneratedValues.Where(c => !insertList.Contains($"\"{c}\"", StringComparison.Ordinal)).ToList();

        Assert.That(missing, Is.Empty,
            "the generation-delete trigger's INSERT must name every column in RetiredGeneratedValueBulkColumns; add a migration that replaces the function: " +
            string.Join(", ", missing));
    }

    // ---- Reads and the purge ----

    [Test]
    public async Task GetRetiredGeneratedValuesInUseAsync_MatchesCaseInsensitivelyOnTheGivenAttributeOnlyAsync()
    {
        var estate = await SeedEstateAsync();
        await using (var seed = NewContext())
        {
            seed.RetiredGeneratedValues.Add(Entry(estate.MvTextAttributeId, "Joe.Bloggs"));
            seed.RetiredGeneratedValues.Add(Entry(estate.MvSecondTextAttributeId, "ada.lovelace"));
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var taken = await NewSyncRepository(ctx).GetRetiredGeneratedValuesInUseAsync(estate.MvTextAttributeId, null, ["joe.bloggs", "ada.lovelace", "free.value"]);

        Assert.That(taken, Is.EquivalentTo(new[] { "joe.bloggs" }));
    }

    [Test]
    public async Task DeleteRetiredGeneratedValuesForAttributeAsync_ForgetsOnlyThatAttributeAndCountsAsync()
    {
        var estate = await SeedEstateAsync();
        await using (var seed = NewContext())
        {
            seed.RetiredGeneratedValues.AddRange(Entry(estate.MvTextAttributeId, "a"), Entry(estate.MvTextAttributeId, "b"), Entry(estate.MvSecondTextAttributeId, "c"));
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var forgotten = await NewSyncRepository(ctx).DeleteRetiredGeneratedValuesForAttributeAsync(estate.MvTextAttributeId, null);

        await using var check = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forgotten, Is.EqualTo(2));
            Assert.That(await check.RetiredGeneratedValues.CountAsync(r => r.MetaverseAttributeId == estate.MvSecondTextAttributeId), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task GetRetiredGeneratedValueHeadersRangeAsync_SearchesPagesAndSaysWhetherTheHolderStillExistsAsync()
    {
        var estate = await SeedEstateAsync();
        var activityId = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            var held = Entry(estate.MvTextAttributeId, "marisol.fenwick1");
            held.FromObjectId = estate.MvoId;
            held.FromObjectDisplayName = "Marisol Fenwick";
            held.RetiredAt = DateTime.UtcNow.AddDays(-3);
            held.ActivityId = activityId;
            var gone = Entry(estate.MvTextAttributeId, "t.ng");
            gone.FromObjectId = Guid.NewGuid();
            gone.FromObjectDisplayName = "Tomas Ng";
            gone.RetiredAt = DateTime.UtcNow.AddDays(-30);
            seed.RetiredGeneratedValues.AddRange(held, gone, Entry(estate.MvSecondTextAttributeId, "fenwick.elsewhere"));
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var repo = NewSyncRepository(ctx);
        var (all, total) = await repo.GetRetiredGeneratedValueHeadersRangeAsync(estate.MvTextAttributeId, null, null, 0, 50, includeTotalCount: true);
        var (searched, searchedTotal) = await repo.GetRetiredGeneratedValueHeadersRangeAsync(estate.MvTextAttributeId, null, "FENWICK", 0, 50, includeTotalCount: true);
        var (secondPage, uncounted) = await repo.GetRetiredGeneratedValueHeadersRangeAsync(estate.MvTextAttributeId, null, null, 1, 1, includeTotalCount: false);
        var byName = (await repo.GetRetiredGeneratedValueHeadersRangeAsync(estate.MvTextAttributeId, null, "tomas", 0, 50, includeTotalCount: false)).Items;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(total, Is.EqualTo(2));
            Assert.That(all.Select(h => h.Value), Is.EqualTo(new[] { "marisol.fenwick1", "t.ng" }), "newest first");
            Assert.That(all[0].FromObjectExists, Is.True);
            Assert.That(all[0].FromObjectTypeName, Is.EqualTo(estate.MvoTypeName));
            Assert.That(all[0].FromObjectTypePluralName, Is.EqualTo(estate.MvoTypeName.Replace("Person-", "People-")), "what a link to the holder is built from");
            Assert.That(all[1].FromObjectTypePluralName, Is.Null, "a deleted holder has no link");
            Assert.That(all[0].ActivityId, Is.EqualTo(activityId));
            Assert.That(all[0].AttributeName, Is.EqualTo(estate.MvTextAttributeName));
            Assert.That(all[1].FromObjectExists, Is.False);
            Assert.That(all[1].FromObjectDisplayName, Is.EqualTo("Tomas Ng"));
            Assert.That(searched.Select(h => h.Value), Is.EqualTo(new[] { "marisol.fenwick1" }), "case-insensitive, and only this attribute");
            Assert.That(searchedTotal, Is.EqualTo(1));
            Assert.That(secondPage.Select(h => h.Value), Is.EqualTo(new[] { "t.ng" }));
            Assert.That(uncounted, Is.Null, "not counted, never zero");
            Assert.That(byName.Select(h => h.Value), Is.EqualTo(new[] { "t.ng" }), "the holder's name is searchable too");
        }
    }

    [Test]
    public async Task GetRetiredGeneratedValueCountsAsync_GroupsPerAttributeAsync()
    {
        var estate = await SeedEstateAsync();
        await using (var seed = NewContext())
        {
            seed.RetiredGeneratedValues.AddRange(Entry(estate.MvTextAttributeId, "a"), Entry(estate.MvTextAttributeId, "b"));
            seed.RetiredGeneratedValues.Add(new RetiredGeneratedValue
            {
                ConnectedSystemObjectTypeAttributeId = estate.CsTextAttributeId, Value = "x", NormalisedValue = "x",
                RetiredAt = DateTime.UtcNow, Reason = RetiredGeneratedValueReason.ObjectDeleted
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var counts = await NewSyncRepository(ctx).GetRetiredGeneratedValueCountsAsync([estate.MvTextAttributeId, estate.MvSecondTextAttributeId], [estate.CsTextAttributeId]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counts.Single(c => c.MetaverseAttributeId == estate.MvTextAttributeId).Count, Is.EqualTo(2));
            Assert.That(counts.Any(c => c.MetaverseAttributeId == estate.MvSecondTextAttributeId), Is.False, "an attribute with none is absent");
            Assert.That(counts.Single(c => c.ConnectedSystemObjectTypeAttributeId == estate.CsTextAttributeId).Count, Is.EqualTo(1));
        }
    }

    // ---- Helpers ----

    private static RetiredGeneratedValue Entry(int metaverseAttributeId, string value) => new()
    {
        MetaverseAttributeId = metaverseAttributeId,
        Value = value,
        NormalisedValue = value.ToLowerInvariant(),
        RetiredAt = DateTime.UtcNow,
        Reason = RetiredGeneratedValueReason.ObjectDeleted
    };

    private async Task<RetiredGeneratedValue> SingleRetiredAsync(int? mvAttributeId = null, int? csAttributeId = null)
    {
        await using var ctx = NewContext();
        return await ctx.RetiredGeneratedValues.SingleAsync(r =>
            r.MetaverseAttributeId == mvAttributeId && r.ConnectedSystemObjectTypeAttributeId == csAttributeId);
    }

    private async Task<Guid> AddImportAssignmentAsync(Estate estate, string value)
    {
        await using var ctx = NewContext();
        var assignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = estate.MvoId, MetaverseAttributeId = estate.MvTextAttributeId,
            Value = value, NormalisedValue = value.ToLowerInvariant(), State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = estate.ImportGenerationId, Created = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
        };
        ctx.GeneratedValueAssignments.Add(assignment);
        await ctx.SaveChangesAsync();
        return assignment.Id;
    }

    private async Task AddExportAssignmentAsync(Estate estate, string value)
    {
        await using var ctx = NewContext();
        ctx.GeneratedValueAssignments.Add(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), ConnectedSystemObjectId = estate.CsoId, ConnectedSystemObjectTypeAttributeId = estate.CsTextAttributeId,
            Value = value, NormalisedValue = value.ToLowerInvariant(), State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = estate.ExportGenerationId, Created = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// A Metaverse Object named "Joe Bloggs" with an import generation on a Text attribute, and a Connected System
    /// Object named "Joe Bloggs (directory)" with an export generation on a Text attribute; each run gets its own
    /// attributes and rule, so tests never see one another's register.
    /// </summary>
    private async Task<Estate> SeedEstateAsync(bool neverReuse = true, GeneratedValueTokenKind tokenKind = GeneratedValueTokenKind.OnlyIfTaken)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var ctx = NewContext();

        var mvoType = new MetaverseObjectType { Name = $"Person-{suffix}", PluralName = $"People-{suffix}" };
        var mvTextAttribute = new MetaverseAttribute { Name = $"Account Name-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var mvSecondTextAttribute = new MetaverseAttribute { Name = $"Email-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        ctx.MetaverseObjectTypes.Add(mvoType);
        ctx.MetaverseAttributes.AddRange(mvTextAttribute, mvSecondTextAttribute);

        var connectorDefinition = new ConnectorDefinition { Name = $"def-{suffix}" };
        var system = new ConnectedSystem { Name = $"system-{suffix}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var csTextAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "accountName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var csNameAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(csTextAttribute);
        csType.Attributes.Add(csNameAttribute);
        ctx.AddRange(connectorDefinition, system, csType);
        await ctx.SaveChangesAsync();

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow, CachedDisplayName = "Joe Bloggs" };
        var cso = new ConnectedSystemObject
        {
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            DateJoined = DateTime.UtcNow,
            ExternalIdAttributeId = csTextAttribute.Id
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = csNameAttribute, StringValue = "Joe Bloggs (directory)" });
        ctx.MetaverseObjects.Add(mvo);
        ctx.ConnectedSystemObjects.Add(cso);
        await ctx.SaveChangesAsync();

        var importRule = new SyncRule
        {
            Name = $"import-{suffix}", Direction = SyncRuleDirection.Import, ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        var exportRule = new SyncRule
        {
            Name = $"export-{suffix}", Direction = SyncRuleDirection.Export, ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        ctx.SyncRules.AddRange(importRule, exportRule);
        await ctx.SaveChangesAsync();

        var importMapping = new SyncRuleMapping { SyncRuleId = importRule.Id, TargetMetaverseAttributeId = mvTextAttribute.Id };
        var exportMapping = new SyncRuleMapping { SyncRuleId = exportRule.Id, TargetConnectedSystemAttributeId = csTextAttribute.Id };
        ctx.SyncRuleMappings.AddRange(importMapping, exportMapping);
        await ctx.SaveChangesAsync();

        var importGeneration = new SyncRuleMappingGeneration { SyncRuleMappingId = importMapping.Id, TokenKind = tokenKind, NeverReuse = neverReuse };
        var exportGeneration = new SyncRuleMappingGeneration { SyncRuleMappingId = exportMapping.Id, TokenKind = tokenKind, NeverReuse = neverReuse };
        ctx.SyncRuleMappingGenerations.AddRange(importGeneration, exportGeneration);
        await ctx.SaveChangesAsync();

        return new Estate(mvo.Id, mvoType.Name, mvTextAttribute.Id, mvTextAttribute.Name, mvSecondTextAttribute.Id, cso.Id, system.Id,
            csTextAttribute.Id, importRule.Id, importMapping.Id, importGeneration.Id, exportGeneration.Id);
    }

    private sealed record Estate(
        Guid MvoId,
        string MvoTypeName,
        int MvTextAttributeId,
        string MvTextAttributeName,
        int MvSecondTextAttributeId,
        Guid CsoId,
        int ConnectedSystemId,
        int CsTextAttributeId,
        int ImportRuleId,
        int ImportMappingId,
        int ImportGenerationId,
        int ExportGenerationId);
}
