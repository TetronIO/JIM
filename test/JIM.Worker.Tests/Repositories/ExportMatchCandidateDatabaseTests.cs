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
/// Real-PostgreSQL verification of the batch export-matching candidate query added to batch export
/// matching per page instead of one query per Metaverse Object:
/// <c>ConnectedSystemRepository.GetExportMatchCandidateIdsAsync</c> (the batch lookup),
/// <c>GetConnectedSystemObjectForExportMatchAsync</c> (hydration of a candidate), and the per-object
/// <c>FindConnectedSystemObjectUsingMatchingRuleAsync</c>'s wildcard fix (case-insensitive comparison no
/// longer uses <c>EF.Functions.ILike</c>, so <c>%</c>, <c>_</c> and <c>\</c> in a value no longer act as
/// wildcards). The in-memory provider cannot prove any of the raw-SQL predicates (typed array
/// unnesting via <c>unnest(@values)</c>, the name-based join to <c>ConnectedSystemAttributes</c>,
/// PostgreSQL's <c>lower()</c>-based exact case-insensitive equality) or EF's translation of
/// <c>ILike</c>/<c>ToLower()</c>, so only a real database run proves the query text is correct.
/// </summary>
/// <remarks>
/// Seeding pattern mirrors <see cref="ExportMatchingClaimDatabaseTests"/>: every entity a test writes
/// (Connector Definition, Connected System, Object Type, Attribute, Connected System Object, Metaverse
/// Object) is created and saved through ONE <c>seed</c> context per test, never split across
/// separately-disposed contexts. EF's <c>Add()</c>/<c>AddRange()</c> walks the whole reachable entity
/// graph and (re-)inserts every navigation it finds untracked (`src/CLAUDE.md` &gt; "DbSet.Add Walks the
/// Graph"); a detached, already-persisted parent obtained from a disposed context is exactly such an
/// untracked navigation, so adding a child that still references it re-attempts the parent's insert and
/// fails with a duplicate key on its primary key. Keeping the whole seed on one context means a later
/// `Add()` finds the parent already tracked (Unchanged) and stops the walk there. Only the read-only
/// calls under test (against the batch query, hydration, or the per-object match) use a second, fresh
/// context, matching how the repository is actually used in production (a query, never a write).
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ExportMatchCandidateDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL export match candidate tests.");

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
    /// Builds and saves a Connected System and a single Object Type carrying one attribute of the given
    /// data type, on the caller's own context. The caller must use the SAME context for any further
    /// entity creation that references the returned entities (see the class remarks).
    /// </summary>
    private static async Task<(ConnectedSystem System, ConnectedSystemObjectType Type, ConnectedSystemObjectTypeAttribute Attribute)> SeedSystemAndTypeAsync(
        JimDbContext seed, AttributeDataType dataType, string attributeName = "employeeId", string systemName = "Yellowstone Target")
    {
        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid()}", BuiltIn = true };
        var system = new ConnectedSystem { Name = systemName, ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = attributeName,
            ConnectedSystemObjectType = csType,
            Type = dataType,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true
        };
        csType.Attributes.Add(attribute);

        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();

        return (system, csType, attribute);
    }

    private static ConnectedSystemObject CreateCso(
        ConnectedSystem system,
        ConnectedSystemObjectType type,
        ConnectedSystemObjectTypeAttribute attribute,
        object value,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal,
        Guid? metaverseObjectId = null)
    {
        var av = new ConnectedSystemObjectAttributeValue { Attribute = attribute };
        switch (value)
        {
            case string s: av.StringValue = s; break;
            case int i: av.IntValue = i; break;
            case long l: av.LongValue = l; break;
            case decimal d: av.DecimalValue = d; break;
            case Guid g: av.GuidValue = g; break;
        }

        return new ConnectedSystemObject
        {
            Type = type,
            ConnectedSystem = system,
            Status = status,
            JoinType = metaverseObjectId.HasValue ? ConnectedSystemObjectJoinType.Joined : ConnectedSystemObjectJoinType.NotJoined,
            MetaverseObjectId = metaverseObjectId,
            ExternalIdAttributeId = attribute.Id,
            AttributeValues = [av]
        };
    }

    #region Batch candidate query: one row per supported type

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_TextType_ReturnsMatchAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);
        var cso = CreateCso(system, type, attribute, "E12345");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.EqualTo(new[] { ((object)"E12345", cso.Id) }));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_NumberType_ReturnsMatchAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Number);
        var cso = CreateCso(system, type, attribute, 42);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Number, caseSensitive: true, values: [42]);

        Assert.That(result, Is.EqualTo(new[] { ((object)42, cso.Id) }));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_LongNumberType_ReturnsMatchAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.LongNumber);
        var cso = CreateCso(system, type, attribute, 9_000_000_000L);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.LongNumber, caseSensitive: true, values: [9_000_000_000L]);

        Assert.That(result, Is.EqualTo(new[] { ((object)9_000_000_000L, cso.Id) }));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_DecimalType_ScaleInsensitiveMatchAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Decimal);
        var cso = CreateCso(system, type, attribute, 5.00m);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // PostgreSQL numeric equality is scale-insensitive: 5.0 must match a stored 5.00.
        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Decimal, caseSensitive: true, values: [5.0m]);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].ConnectedSystemObjectId, Is.EqualTo(cso.Id));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_GuidType_ReturnsMatchAsync()
    {
        var guid = Guid.NewGuid();

        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Guid);
        var cso = CreateCso(system, type, attribute, guid);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Guid, caseSensitive: true, values: [guid]);

        Assert.That(result, Is.EqualTo(new[] { ((object)guid, cso.Id) }));
    }

    #endregion

    #region Case sensitivity: exact equality, no wildcards

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_CaseInsensitive_MatchesDifferentCasingAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);
        var cso = CreateCso(system, type, attribute, "ESMITH");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Text, caseSensitive: false, values: ["esmith"]);

        Assert.That(result, Is.EqualTo(new[] { ((object)"esmith", cso.Id) }));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_CaseInsensitive_UnderscoreDoesNotWildcardMatchAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);
        // "jxsmith" would match the ILIKE pattern "j_smith" (the `_` wildcard standing for "x"); the
        // batch query must not exhibit that bug.
        var cso = CreateCso(system, type, attribute, "jxsmith");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Text, caseSensitive: false, values: ["j_smith"]);

        Assert.That(result, Is.Empty, "an underscore in the value must not act as a single-character wildcard");
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_CaseInsensitive_PercentDoesNotWildcardMatchAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);
        var cso = CreateCso(system, type, attribute, "anything-at-all");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Text, caseSensitive: false, values: ["%"]);

        Assert.That(result, Is.Empty, "a percent sign in the value must not act as a wildcard matching any string");
    }

    #endregion

    #region Eligibility exclusions

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_JoinedCso_IsExcludedAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);

        var mvo = new MetaverseObject { Type = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true } };
        seed.Add(mvo);
        await seed.SaveChangesAsync();

        var cso = CreateCso(system, type, attribute, "E12345", metaverseObjectId: mvo.Id);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_ObsoleteCso_IsExcludedAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);
        var cso = CreateCso(system, type, attribute, "E12345", status: ConnectedSystemObjectStatus.Obsolete);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_PendingProvisioningCso_IsExcludedAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);
        var cso = CreateCso(system, type, attribute, "E12345", status: ConnectedSystemObjectStatus.PendingProvisioning);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_OtherConnectedSystem_IsExcludedAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);
        var (otherSystem, otherType, otherAttribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text, systemName: "Other System");

        var cso = CreateCso(otherSystem, otherType, otherAttribute, "E12345");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_OtherObjectType_IsExcludedAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);

        var otherType = new ConnectedSystemObjectType { Name = "GROUP", ConnectedSystem = system, Selected = true };
        var otherAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = attribute.Name, ConnectedSystemObjectType = otherType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        otherType.Attributes.Add(otherAttribute);
        seed.Add(otherType);
        await seed.SaveChangesAsync();

        var cso = CreateCso(system, otherType, otherAttribute, "E12345");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_OtherAttributeName_IsExcludedAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);

        var otherAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "differentAttribute", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        seed.Add(otherAttribute);
        await seed.SaveChangesAsync();

        var cso = CreateCso(system, type, otherAttribute, "E12345");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.Empty);
    }

    #endregion

    #region Ordering and value identity

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_MultipleValues_OrderedByValueThenCsoIdAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);

        var csoB = CreateCso(system, type, attribute, "B");
        var csoA = CreateCso(system, type, attribute, "A");
        seed.AddRange(csoB, csoA);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Text, caseSensitive: true, values: ["B", "A"]);

        Assert.That(result, Is.EqualTo(new[] { ((object)"A", csoA.Id), ((object)"B", csoB.Id) }));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_MultipleCsosMatchSameValue_OrderedByCsoIdAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);

        var first = CreateCso(system, type, attribute, "E12345");
        var second = CreateCso(system, type, attribute, "E12345");
        seed.AddRange(first, second);
        await seed.SaveChangesAsync();

        var (expectedFirstId, expectedSecondId) = first.Id.CompareTo(second.Id) <= 0
            ? (first.Id, second.Id) : (second.Id, first.Id);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Text, caseSensitive: true, values: ["E12345"]);

        Assert.That(result, Is.EqualTo(new[] { ((object)"E12345", expectedFirstId), ((object)"E12345", expectedSecondId) }));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_ReturnedValue_IsTheExactInputElementAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);
        var cso = CreateCso(system, type, attribute, "E12345");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        // A distinct string instance carrying the same characters: PostgreSQL returns a value read back
        // from the wire (never the same CLR reference as the parameter), so this proves the query
        // returns the parameter's logical value, not that it happens to reuse the same object.
        var inputValue = new string("E12345".ToCharArray());

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Text, caseSensitive: true, values: [inputValue]);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Value, Is.EqualTo("E12345"));
    }

    #endregion

    #region Empty input / unsupported type

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_EmptyValues_ReturnsEmptyWithoutQueryingAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
            system.Id, type.Id, attribute.Name, AttributeDataType.Text, caseSensitive: true, values: []);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void GetExportMatchCandidateIdsAsync_UnsupportedDataType_ThrowsArgumentException()
    {
        using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        Assert.That(
            () => repository.ConnectedSystems.GetExportMatchCandidateIdsAsync(
                1, 1, "attr", AttributeDataType.Boolean, caseSensitive: true, values: ["x"]),
            Throws.ArgumentException);
    }

    #endregion

    #region Hydration

    [Test]
    public async Task GetConnectedSystemObjectForExportMatchAsync_EligibleCso_ReturnsEntityWithAttributeValuesAndAttributesLoadedAndTrackedAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);
        var cso = CreateCso(system, type, attribute, "E12345");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectForExportMatchAsync(cso.Id);

        Assert.That(result, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.Id, Is.EqualTo(cso.Id));
            Assert.That(result.AttributeValues, Has.Count.EqualTo(1));
            Assert.That(result.AttributeValues[0].Attribute, Is.Not.Null);
            Assert.That(result.AttributeValues[0].Attribute.Name, Is.EqualTo(attribute.Name));
            Assert.That(ctx.Entry(result).State, Is.Not.EqualTo(EntityState.Detached), "the result must be tracked, not AsNoTracking, so a caller can fix it up after an atomic claim");
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectForExportMatchAsync_AlreadyJoined_ReturnsNullAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);

        var mvo = new MetaverseObject { Type = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true } };
        seed.Add(mvo);
        await seed.SaveChangesAsync();

        var cso = CreateCso(system, type, attribute, "E12345", metaverseObjectId: mvo.Id);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectForExportMatchAsync(cso.Id);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetConnectedSystemObjectForExportMatchAsync_NotNormalStatus_ReturnsNullAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);
        var cso = CreateCso(system, type, attribute, "E12345", status: ConnectedSystemObjectStatus.Obsolete);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectForExportMatchAsync(cso.Id);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetConnectedSystemObjectForExportMatchAsync_NotFound_ReturnsNullAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectForExportMatchAsync(Guid.NewGuid());

        Assert.That(result, Is.Null);
    }

    #endregion

    #region Per-object method: wildcard fix

    [Test]
    public async Task FindConnectedSystemObjectUsingMatchingRuleAsync_CaseInsensitiveUnderscoreValue_DoesNotWildcardMatchAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);
        // "jxsmith" would previously match the ILIKE pattern "j_smith" (the `_` standing for "x").
        var cso = CreateCso(system, type, attribute, "jxsmith");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        var metaverseAttribute = new MetaverseAttribute { Id = 9001, Name = "Employee Id", Type = AttributeDataType.Text };
        var rule = new ObjectMatchingRule
        {
            Id = 1,
            CaseSensitive = false,
            TargetMetaverseAttribute = metaverseAttribute,
            TargetMetaverseAttributeId = metaverseAttribute.Id,
            Sources =
            [
                new ObjectMatchingRuleSource { ConnectedSystemAttribute = attribute, ConnectedSystemAttributeId = attribute.Id }
            ]
        };

        var mvo = new MetaverseObject
        {
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = metaverseAttribute.Id, StringValue = "j_smith" }]
        };

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.FindConnectedSystemObjectUsingMatchingRuleAsync(mvo, system, type, rule);

        Assert.That(result, Is.Null, "j_smith must not wildcard-match jxsmith now that exact case-insensitive equality replaces EF.Functions.ILike");
    }

    [Test]
    public async Task FindConnectedSystemObjectUsingMatchingRuleAsync_CaseInsensitiveExactMatch_StillMatchesAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);
        var cso = CreateCso(system, type, attribute, "ESMITH");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        var metaverseAttribute = new MetaverseAttribute { Id = 9002, Name = "Employee Id", Type = AttributeDataType.Text };
        var rule = new ObjectMatchingRule
        {
            Id = 1,
            CaseSensitive = false,
            TargetMetaverseAttribute = metaverseAttribute,
            TargetMetaverseAttributeId = metaverseAttribute.Id,
            Sources =
            [
                new ObjectMatchingRuleSource { ConnectedSystemAttribute = attribute, ConnectedSystemAttributeId = attribute.Id }
            ]
        };

        var mvo = new MetaverseObject
        {
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = metaverseAttribute.Id, StringValue = "esmith" }]
        };

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.FindConnectedSystemObjectUsingMatchingRuleAsync(mvo, system, type, rule);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(cso.Id));
    }

    #endregion
}
