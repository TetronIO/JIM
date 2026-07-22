// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the attribute-presence filter (the <c>hasAttribute:</c> search, issue #1040) added to
/// <see cref="JIM.Application.Servers.MetaverseServer.GetMetaverseObjectHeadersPagedAsync"/>. The filter is a raw-SQL
/// EXISTS sub-query the EF Core in-memory provider cannot execute, so its semantics (single- vs multi-valued, and that
/// the predicate matches the deletion-impact count which includes asserted-null marker rows) are only verifiable
/// against a real database. Opt-in via the shared <c>JIM_TEST_RESET_*</c> variables; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseObjectHasAttributePresenceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL attribute-presence tests.");

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

    private record SeedIds(int TypeId, int SerialNumberAttrId, int TagAttrId);

    /// <summary>
    /// Seeds a Device type with a single-valued "serialNumber" and a multi-valued "tag" attribute, and five devices:
    /// D1 (serialNumber only), D2 (two tag values, no serialNumber), D3 (serialNumber and one tag),
    /// D4 (neither; a bare object), D5 (an asserted-null serialNumber marker row: NullValue with no stored value).
    /// </summary>
    private async Task<SeedIds> SeedAsync()
    {
        await using var ctx = NewContext();
        var type = new MetaverseObjectType { Name = "Device", PluralName = "Devices", BuiltIn = false };
        var serialNumber = new MetaverseAttribute { Name = "serialNumber", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = false };
        var tag = new MetaverseAttribute { Name = "tag", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued, BuiltIn = false };
        type.Attributes.Add(serialNumber);
        type.Attributes.Add(tag);

        var d1 = new MetaverseObject { Type = type, CachedDisplayName = "D1" };
        d1.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = serialNumber, StringValue = "SN1" });

        var d2 = new MetaverseObject { Type = type, CachedDisplayName = "D2" };
        d2.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = tag, StringValue = "alpha" });
        d2.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = tag, StringValue = "beta" });

        var d3 = new MetaverseObject { Type = type, CachedDisplayName = "D3" };
        d3.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = serialNumber, StringValue = "SN3" });
        d3.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = tag, StringValue = "gamma" });

        var d4 = new MetaverseObject { Type = type, CachedDisplayName = "D4" };

        // Asserted null: a positively-asserted "no value" marker row for serialNumber. Admin-facing observability views
        // (this filter and the deletion-impact counts) treat it as present, so D5 matches a serialNumber presence filter.
        var d5 = new MetaverseObject { Type = type, CachedDisplayName = "D5" };
        d5.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = serialNumber, NullValue = true });

        ctx.MetaverseObjectTypes.Add(type);
        ctx.MetaverseObjects.AddRange(d1, d2, d3, d4, d5);
        await ctx.SaveChangesAsync();

        return new SeedIds(type.Id, serialNumber.Id, tag.Id);
    }

    private async Task<int> PersistSearchAsync(int typeId)
    {
        await using var ctx = NewContext();
        var type = await ctx.MetaverseObjectTypes.AsTracking().SingleAsync(t => t.Id == typeId);
        var search = new PredefinedSearch { Name = "Device Query", Uri = "device-query", MetaverseObjectType = type };
        ctx.PredefinedSearches.Add(search);
        await ctx.SaveChangesAsync();
        return search.Id;
    }

    private async Task<List<string>> RunAndGetNamesAsync(int searchId, int? hasAttributeId)
    {
        await using var ctx = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var search = await jim.Search.GetPredefinedSearchAsync(searchId);
        Assert.That(search, Is.Not.Null);
        var result = await jim.Metaverse.GetMetaverseObjectHeadersPagedAsync(search!, 1, 100, hasAttributeId: hasAttributeId);
        return result.Results.Select(r => r.CachedDisplayName!).OrderBy(n => n).ToList();
    }

    [Test]
    public async Task HasAttributePresence_NoFilter_ReturnsAllObjectsOfTypeAsync()
    {
        var ids = await SeedAsync();
        var searchId = await PersistSearchAsync(ids.TypeId);
        Assert.That(await RunAndGetNamesAsync(searchId, hasAttributeId: null),
            Is.EqualTo(new[] { "D1", "D2", "D3", "D4", "D5" }));
    }

    [Test]
    public async Task HasAttributePresence_SingleValued_ReturnsOnlyObjectsHoldingAValueIncludingAssertedNullAsync()
    {
        var ids = await SeedAsync();
        var searchId = await PersistSearchAsync(ids.TypeId);
        // D1 and D3 hold a serialNumber value; D5 holds an asserted-null serialNumber marker (present for this filter);
        // D2 and D4 hold none, so are excluded.
        Assert.That(await RunAndGetNamesAsync(searchId, hasAttributeId: ids.SerialNumberAttrId),
            Is.EqualTo(new[] { "D1", "D3", "D5" }));
    }

    [Test]
    public async Task HasAttributePresence_MultiValued_CountsAnObjectOnceAsync()
    {
        var ids = await SeedAsync();
        var searchId = await PersistSearchAsync(ids.TypeId);
        // D2 holds two tag values but must appear once; D3 holds one; D1/D4/D5 hold none.
        Assert.That(await RunAndGetNamesAsync(searchId, hasAttributeId: ids.TagAttrId),
            Is.EqualTo(new[] { "D2", "D3" }));
    }

    [Test]
    public async Task GetMetaverseAttributeAsync_ByName_ResolvesCaseInsensitivelyAsync()
    {
        var ids = await SeedAsync();
        await using var ctx = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(ctx));

        // The hasAttribute: filter resolves a typed / URL name to an attribute id; a differing case must still resolve
        // (attribute names are unique case-insensitively), so a mistyped-case deep link finds the objects rather than
        // showing the "unknown attribute" empty state.
        var upper = await jim.Metaverse.GetMetaverseAttributeAsync("SERIALNUMBER");
        var lower = await jim.Metaverse.GetMetaverseAttributeAsync("serialnumber");

        Assert.That(upper, Is.Not.Null);
        Assert.That(upper!.Id, Is.EqualTo(ids.SerialNumberAttrId));
        Assert.That(lower, Is.Not.Null);
        Assert.That(lower!.Id, Is.EqualTo(ids.SerialNumberAttrId));
    }
}
