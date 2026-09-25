// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that three case-insensitive comparisons in
/// <see cref="MetaverseRepository"/> use exact equality rather than <c>EF.Functions.ILike</c>, which
/// treats <c>%</c>, <c>_</c> and <c>\</c> in the compared value as wildcards:
/// <list type="bullet">
/// <item><description><c>FindMetaverseObjectUsingMatchingRuleAsync</c>'s case-insensitive Text branch
/// (the inbound Connected System Object to Metaverse Object join); a Connected System Object being
/// joined to the wrong Metaverse Object because of a stray <c>_</c> or <c>%</c> in its value is a
/// data-integrity defect, not a cosmetic one.</description></item>
/// <item><description><c>GetMetaverseObjectsCountAsync</c>'s exact attribute-value filter (shared by
/// <c>GetMetaverseObjectsAsync</c>), reachable via the REST API's <c>filterAttributeName</c> /
/// <c>filterAttributeValue</c> query parameters and <c>Get-JIMMetaverseObject -AttributeName
/// -AttributeValue</c>.</description></item>
/// <item><description><c>GetMetaverseObjectTypeAsync(string, ...)</c> and
/// <c>GetMetaverseObjectTypeByPluralNameAsync</c> (lookup by name / plural name).</description></item>
/// </list>
/// Mirrors the export-side fix and its test in <see cref="ExportMatchCandidateDatabaseTests"/>
/// (<c>ConnectedSystemRepository.FindConnectedSystemObjectUsingMatchingRuleAsync</c>). The in-memory
/// provider cannot prove any of this: it does not honour PostgreSQL's <c>ILIKE</c> wildcard semantics
/// the way the real database does, so a test built on it would pass whether or not the bug was present.
/// </summary>
/// <remarks>
/// Seeding pattern mirrors <see cref="ExportMatchCandidateDatabaseTests"/>: every entity a test writes
/// is created and saved through ONE <c>seed</c> context, never split across separately-disposed
/// contexts (`src/CLAUDE.md` &gt; "DbSet.Add Walks the Graph"). Only the read-only call under test uses
/// a second, fresh context.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseExactCaseInsensitiveMatchingDatabaseTests
{
    private string _connectionString = null!;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL exact case-insensitive matching tests.");

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

    /// <summary>
    /// Persists a Metaverse Object Type and one Text Metaverse Attribute on the caller's own context.
    /// </summary>
    private static async Task<(MetaverseObjectType Type, MetaverseAttribute Attribute)> SeedTypeAndAttributeAsync(
        JimDbContext seed, string typeName = "Widget", string pluralName = "Widgets", string attributeName = "Employee Id")
    {
        var type = new MetaverseObjectType { Name = typeName, PluralName = pluralName, BuiltIn = false };
        var attribute = new MetaverseAttribute { Name = attributeName, Type = AttributeDataType.Text };
        seed.AddRange(type, attribute);
        await seed.SaveChangesAsync();
        return (type, attribute);
    }

    private static MetaverseObject CreateMvo(MetaverseObjectType type, MetaverseAttribute attribute, string value)
    {
        return new MetaverseObject
        {
            Type = type,
            AttributeValues = [new MetaverseObjectAttributeValue { Attribute = attribute, AttributeId = attribute.Id, StringValue = value }]
        };
    }

    #region Inbound Object Matching (FindMetaverseObjectUsingMatchingRuleAsync)

    /// <summary>
    /// Builds an in-memory (unpersisted) Object Matching Rule with a single Text source, and a Connected
    /// System Object carrying the given source value for that source attribute. Neither the Connected
    /// System attribute nor the rule need to be persisted: the method under test reads them directly off
    /// the object graph it is handed and only queries the database for candidate Metaverse Objects.
    /// </summary>
    private static (ObjectMatchingRule Rule, ConnectedSystemObject Cso) BuildInboundMatchInputs(
        MetaverseAttribute targetAttribute, string sourceValue, bool caseSensitive)
    {
        var csAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 501,
            Name = "employeeId",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            CaseSensitive = caseSensitive,
            TargetMetaverseAttribute = targetAttribute,
            TargetMetaverseAttributeId = targetAttribute.Id,
            Sources = [new ObjectMatchingRuleSource { ConnectedSystemAttribute = csAttribute, ConnectedSystemAttributeId = csAttribute.Id }]
        };

        var cso = new ConnectedSystemObject
        {
            AttributeValues = [new ConnectedSystemObjectAttributeValue { Attribute = csAttribute, AttributeId = csAttribute.Id, StringValue = sourceValue }]
        };

        return (rule, cso);
    }

    [Test]
    public async Task FindMetaverseObjectUsingMatchingRuleAsync_CaseInsensitiveUnderscoreValue_DoesNotWildcardMatchAsync()
    {
        await using var seed = NewContext();
        var (type, attribute) = await SeedTypeAndAttributeAsync(seed);
        // "jxsmith" would previously match the ILIKE pattern "j_smith" (the `_` standing for "x").
        var mvo = CreateMvo(type, attribute, "jxsmith");
        seed.Add(mvo);
        await seed.SaveChangesAsync();

        var (rule, cso) = BuildInboundMatchInputs(attribute, sourceValue: "j_smith", caseSensitive: false);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(cso, type, rule);

        Assert.That(result, Is.Null, "j_smith must not wildcard-match jxsmith now that exact case-insensitive equality replaces EF.Functions.ILike");
    }

    [Test]
    public async Task FindMetaverseObjectUsingMatchingRuleAsync_CaseInsensitivePercentValue_DoesNotWildcardMatchAsync()
    {
        await using var seed = NewContext();
        var (type, attribute) = await SeedTypeAndAttributeAsync(seed);
        // "aXYZb" would previously match the ILIKE pattern "a%b" (the `%` standing for "XYZ").
        var mvo = CreateMvo(type, attribute, "aXYZb");
        seed.Add(mvo);
        await seed.SaveChangesAsync();

        var (rule, cso) = BuildInboundMatchInputs(attribute, sourceValue: "a%b", caseSensitive: false);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(cso, type, rule);

        Assert.That(result, Is.Null, "a%b must not wildcard-match aXYZb now that exact case-insensitive equality replaces EF.Functions.ILike");
    }

    [Test]
    public async Task FindMetaverseObjectUsingMatchingRuleAsync_CaseInsensitiveExactMatchDifferentCasing_StillMatchesAsync()
    {
        await using var seed = NewContext();
        var (type, attribute) = await SeedTypeAndAttributeAsync(seed);
        var mvo = CreateMvo(type, attribute, "j_smith");
        seed.Add(mvo);
        await seed.SaveChangesAsync();

        var (rule, cso) = BuildInboundMatchInputs(attribute, sourceValue: "J_Smith", caseSensitive: false);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(cso, type, rule);

        Assert.That(result, Is.Not.Null, "J_Smith must still match j_smith on a case-insensitive rule");
        Assert.That(result!.Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public async Task FindMetaverseObjectUsingMatchingRuleAsync_CaseInsensitiveBackslashValue_MatchesIdenticalValueAsync()
    {
        await using var seed = NewContext();
        var (type, attribute) = await SeedTypeAndAttributeAsync(seed);
        // A backslash is ILIKE's escape character; an unescaped one previously risked the identical
        // value failing to match itself.
        var mvo = CreateMvo(type, attribute, "dom\\user");
        seed.Add(mvo);
        await seed.SaveChangesAsync();

        var (rule, cso) = BuildInboundMatchInputs(attribute, sourceValue: "dom\\user", caseSensitive: false);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(cso, type, rule);

        Assert.That(result, Is.Not.Null, "a value containing a backslash must match its identical counterpart");
        Assert.That(result!.Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public async Task FindMetaverseObjectUsingMatchingRuleAsync_CaseSensitiveRule_UnaffectedByFixAsync()
    {
        await using var seed = NewContext();
        var (type, attribute) = await SeedTypeAndAttributeAsync(seed);
        var mvo = CreateMvo(type, attribute, "Smith");
        seed.Add(mvo);
        await seed.SaveChangesAsync();

        // A case-sensitive rule uses plain `==`, not ILike, and was never affected by this bug;
        // differently-cased values must not match.
        var (rule, cso) = BuildInboundMatchInputs(attribute, sourceValue: "smith", caseSensitive: true);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(cso, type, rule);

        Assert.That(result, Is.Null, "a case-sensitive rule must not match differently-cased values");
    }

    #endregion

    #region Exact attribute-value filter (GetMetaverseObjectsCountAsync)

    [Test]
    public async Task GetMetaverseObjectsCountAsync_FilterValueWithUnderscore_DoesNotWildcardMatchAsync()
    {
        await using var seed = NewContext();
        var (type, attribute) = await SeedTypeAndAttributeAsync(seed);
        var mvo = CreateMvo(type, attribute, "jxsmith");
        seed.Add(mvo);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var count = await repository.Metaverse.GetMetaverseObjectsCountAsync(
            filterAttributeName: attribute.Name, filterAttributeValue: "j_smith");

        Assert.That(count, Is.Zero, "j_smith must not wildcard-match jxsmith in the attribute-value filter");
    }

    [Test]
    public async Task GetMetaverseObjectsCountAsync_FilterValueWithPercent_DoesNotWildcardMatchAsync()
    {
        await using var seed = NewContext();
        var (type, attribute) = await SeedTypeAndAttributeAsync(seed);
        var mvo = CreateMvo(type, attribute, "aXYZb");
        seed.Add(mvo);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var count = await repository.Metaverse.GetMetaverseObjectsCountAsync(
            filterAttributeName: attribute.Name, filterAttributeValue: "a%b");

        Assert.That(count, Is.Zero, "a%b must not wildcard-match aXYZb in the attribute-value filter");
    }

    [Test]
    public async Task GetMetaverseObjectsCountAsync_FilterExactValueDifferentCasing_StillMatchesAsync()
    {
        await using var seed = NewContext();
        var (type, attribute) = await SeedTypeAndAttributeAsync(seed);
        var mvo = CreateMvo(type, attribute, "j_smith");
        seed.Add(mvo);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var count = await repository.Metaverse.GetMetaverseObjectsCountAsync(
            filterAttributeName: attribute.Name, filterAttributeValue: "J_SMITH");

        Assert.That(count, Is.EqualTo(1), "the filter must still match the same value case-insensitively");
    }

    #endregion

    #region Metaverse Object Type lookup by name / plural name

    [Test]
    public async Task GetMetaverseObjectTypeAsync_NameWithUnderscore_DoesNotWildcardMatchAsync()
    {
        await using var seed = NewContext();
        var type = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = false };
        seed.Add(type);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // "Gr_up" would previously match the ILIKE pattern against the stored name "Group"
        // (the `_` standing for "o").
        var result = await repository.Metaverse.GetMetaverseObjectTypeAsync("Gr_up", includeChildObjects: false);

        Assert.That(result, Is.Null, "Gr_up must not wildcard-match Group now that exact case-insensitive equality replaces EF.Functions.ILike");
    }

    [Test]
    public async Task GetMetaverseObjectTypeAsync_ExactNameDifferentCasing_StillMatchesAsync()
    {
        await using var seed = NewContext();
        var type = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = false };
        seed.Add(type);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Metaverse.GetMetaverseObjectTypeAsync("GROUP", includeChildObjects: false);

        Assert.That(result, Is.Not.Null, "the lookup must still be case-insensitive for an exact name");
        Assert.That(result!.Id, Is.EqualTo(type.Id));
    }

    [Test]
    public async Task GetMetaverseObjectTypeByPluralNameAsync_PluralNameWithUnderscore_DoesNotWildcardMatchAsync()
    {
        await using var seed = NewContext();
        var type = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = false };
        seed.Add(type);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // "Gr_ups" would previously match the ILIKE pattern against the stored plural name "Groups".
        var result = await repository.Metaverse.GetMetaverseObjectTypeByPluralNameAsync("Gr_ups", includeChildObjects: false);

        Assert.That(result, Is.Null, "Gr_ups must not wildcard-match Groups now that exact case-insensitive equality replaces EF.Functions.ILike");
    }

    [Test]
    public async Task GetMetaverseObjectTypeByPluralNameAsync_ExactPluralNameDifferentCasing_StillMatchesAsync()
    {
        await using var seed = NewContext();
        var type = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = false };
        seed.Add(type);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Metaverse.GetMetaverseObjectTypeByPluralNameAsync("GROUPS", includeChildObjects: false);

        Assert.That(result, Is.Not.Null, "the lookup must still be case-insensitive for an exact plural name");
        Assert.That(result!.Id, Is.EqualTo(type.Id));
    }

    #endregion
}
