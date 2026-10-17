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
/// <c>ConnectedSystemRepository.GetExportMatchCandidateIdsAsync</c> (export matching's lookup, for one
/// Metaverse Object or a page) and <c>GetConnectedSystemObjectForExportMatchAsync</c> (hydration of a
/// candidate). The in-memory provider cannot prove any of the raw-SQL predicates (typed array
/// unnesting via <c>unnest(@values)</c>, the name-based join to <c>ConnectedSystemAttributes</c>,
/// PostgreSQL's <c>lower()</c>-based exact case-insensitive equality, under which <c>%</c>, <c>_</c> and
/// <c>\</c> in a value never act as wildcards), so only a real database run proves the query text is correct.
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

    #region Expression index inputs (#2052)

    [Test]
    public async Task StreamUnjoinedConnectedSystemObjectValues_StreamsOnlyUnjoinedNormalObjectsOfTheTypeWithTheAskedAttributesAsync()
    {
        await using var seed = NewContext();
        var (system, type, employeeId) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);
        var mail = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        type.Attributes.Add(mail);
        var mvo = new MetaverseObject { Type = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true } };
        seed.Add(mvo);
        await seed.SaveChangesAsync();

        var unjoined = CreateCso(system, type, employeeId, "E1");
        unjoined.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = mail, StringValue = "jo@corp.example" });
        unjoined.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = mail, StringValue = "jo.smith@corp.example" });
        var holdsNoneAsked = CreateCso(system, type, employeeId, "E2");
        var joined = CreateCso(system, type, employeeId, "E3", metaverseObjectId: mvo.Id);
        joined.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = mail, StringValue = "joined@corp.example" });
        var obsolete = CreateCso(system, type, employeeId, "E4", status: ConnectedSystemObjectStatus.Obsolete);
        obsolete.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = mail, StringValue = "gone@corp.example" });
        seed.AddRange(unjoined, holdsNoneAsked, joined, obsolete);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var streamed = new List<(Guid Id, List<string?> Mail, List<int> AttributeIds)>();
        await foreach (var (id, values) in repository.ConnectedSystems.StreamUnjoinedConnectedSystemObjectValues(system.Id, type.Id, [mail.Id]))
            streamed.Add((id, values.Select(v => v.StringValue).ToList(), values.Select(v => v.AttributeId).Distinct().ToList()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(streamed.Select(s => s.Id), Is.EquivalentTo(new[] { unjoined.Id, holdsNoneAsked.Id }),
                "every unjoined, Normal object of the type, including one holding none of the asked attributes");
            Assert.That(streamed.Select(s => s.Id), Is.Ordered, "ordered by object, so a reader can build as it streams");
            var unjoinedRow = streamed.Single(s => s.Id == unjoined.Id);
            Assert.That(unjoinedRow.Mail, Is.EquivalentTo(new[] { "jo@corp.example", "jo.smith@corp.example" }));
            Assert.That(unjoinedRow.AttributeIds, Is.EqualTo(new[] { mail.Id }), "only the asked attributes are read");
            Assert.That(streamed.Single(s => s.Id == holdsNoneAsked.Id).Mail, Is.Empty);
        }
    }

    [Test]
    public async Task StreamUnjoinedConnectedSystemObjectValues_EveryValueColumnRoundTripsAsync()
    {
        await using var seed = NewContext();
        var (system, type, text) = await SeedSystemAndTypeAsync(seed, AttributeDataType.Text);
        ConnectedSystemObjectTypeAttribute Add(string name, AttributeDataType dataType)
        {
            var attribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = AttributePlurality.SingleValued, Selected = true
            };
            type.Attributes.Add(attribute);
            return attribute;
        }

        var number = Add("number", AttributeDataType.Number);
        var longNumber = Add("long", AttributeDataType.LongNumber);
        var decimalNumber = Add("decimal", AttributeDataType.Decimal);
        var date = Add("date", AttributeDataType.DateTime);
        var flag = Add("flag", AttributeDataType.Boolean);
        var guid = Add("guid", AttributeDataType.Guid);
        var binary = Add("binary", AttributeDataType.Binary);
        var reference = Add("manager", AttributeDataType.Reference);
        var unresolvedReference = Add("sponsor", AttributeDataType.Reference);
        await seed.SaveChangesAsync();

        var manager = CreateCso(system, type, text, "M1");
        var when = new DateTime(2026, 10, 11, 9, 30, 0, DateTimeKind.Utc);
        var objectGuid = Guid.NewGuid();
        var cso = CreateCso(system, type, text, "E1");
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = number, IntValue = 42 });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = longNumber, LongValue = 9_000_000_000L });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = decimalNumber, DecimalValue = 12.50m });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = date, DateTimeValue = when });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = flag, BoolValue = true });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = guid, GuidValue = objectGuid });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = binary, ByteValue = [1, 2, 3] });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = reference, ReferenceValue = manager });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = unresolvedReference, UnresolvedReferenceValue = "CN=Not Yet Imported" });
        seed.AddRange(manager, cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        IReadOnlyList<ConnectedSystemObjectAttributeValue> values = [];
        await foreach (var (id, streamedValues) in repository.ConnectedSystems.StreamUnjoinedConnectedSystemObjectValues(
                           system.Id, type.Id, [text.Id, number.Id, longNumber.Id, decimalNumber.Id, date.Id, flag.Id, guid.Id, binary.Id, reference.Id, unresolvedReference.Id]))
        {
            if (id == cso.Id)
                values = streamedValues;
        }

        ConnectedSystemObjectAttributeValue Of(ConnectedSystemObjectTypeAttribute attribute) => values.Single(v => v.AttributeId == attribute.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Of(text).StringValue, Is.EqualTo("E1"));
            Assert.That(Of(number).IntValue, Is.EqualTo(42));
            Assert.That(Of(longNumber).LongValue, Is.EqualTo(9_000_000_000L));
            Assert.That(Of(decimalNumber).DecimalValue, Is.EqualTo(12.50m));
            Assert.That(Of(date).DateTimeValue, Is.EqualTo(when));
            Assert.That(Of(flag).BoolValue, Is.True);
            Assert.That(Of(guid).GuidValue, Is.EqualTo(objectGuid));
            Assert.That(Of(binary).ByteValue, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(Of(reference).ReferenceValue?.Id, Is.EqualTo(manager.Id), "a reference reads as the referenced object's id");
            // #2330: a value held only as an unresolved reference is a held value to the held-object check (#2323) and
            // the expression match index (#2052), not the absence of one.
            Assert.That(Of(unresolvedReference).UnresolvedReferenceValue, Is.EqualTo("CN=Not Yet Imported"));
        }
    }

    #endregion
}
