// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the batch secondary external ID lookup added to prefetch a whole
/// import page's Pending Provisioning confirmations in one query per object type instead of one query
/// per unmatched import object: <c>ConnectedSystemRepository.GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync</c>.
/// Matching must be exactly what the existing single-object <c>GetConnectedSystemObjectBySecondaryExternalIdAsync</c>
/// does today (case-sensitive exact <c>StringValue</c> equality, <c>SecondaryExternalIdAttributeId</c> not
/// null, matched against that CSO's OWN configured secondary external id attribute). The in-memory
/// provider cannot prove the raw-SQL predicate (typed array unnesting via <c>unnest(@values)</c>,
/// PostgreSQL's case-sensitive text equality), so only a real database run proves the query text is
/// correct.
/// </summary>
/// <remarks>
/// Seeding pattern mirrors <see cref="ExportMatchCandidateDatabaseTests"/>: every entity a test writes
/// is created and saved through ONE <c>seed</c> context per test, never split across separately-disposed
/// contexts (see that fixture's remarks for why). Only the read-only call under test uses a second,
/// fresh context, matching how the repository is actually used in production.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SecondaryExternalIdMatchCandidateDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL secondary external ID match candidate tests.");

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
    /// Builds and saves a Connected System and a single Object Type carrying one Text attribute
    /// flagged as the secondary external ID, on the caller's own context.
    /// </summary>
    private static async Task<(ConnectedSystem System, ConnectedSystemObjectType Type, ConnectedSystemObjectTypeAttribute SecondaryAttribute)> SeedSystemAndTypeAsync(
        JimDbContext seed, string systemName = "Target System")
    {
        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid()}", BuiltIn = true };
        var system = new ConnectedSystem { Name = systemName, ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var objectGuidAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGuid",
            ConnectedSystemObjectType = csType,
            Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued,
            IsExternalId = true,
            Selected = true
        };
        var secondaryAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName",
            ConnectedSystemObjectType = csType,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            IsSecondaryExternalId = true,
            Selected = true
        };
        csType.Attributes.Add(objectGuidAttribute);
        csType.Attributes.Add(secondaryAttribute);

        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();

        return (system, csType, secondaryAttribute);
    }

    /// <summary>
    /// Creates a CSO with no primary external ID value (the Pending Provisioning state before a
    /// confirming import reveals the target system's real, system-assigned ID), carrying only the
    /// secondary external ID's value.
    /// </summary>
    private static ConnectedSystemObject CreateCso(
        ConnectedSystem system,
        ConnectedSystemObjectType type,
        ConnectedSystemObjectTypeAttribute secondaryAttribute,
        string secondaryValue,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.PendingProvisioning)
    {
        return new ConnectedSystemObject
        {
            Type = type,
            ConnectedSystem = system,
            Status = status,
            // Non-nullable on the model; this test never exercises the primary ID path, so reuse
            // a real, persisted attribute id from the type rather than an arbitrary one.
            ExternalIdAttributeId = secondaryAttribute.Id,
            SecondaryExternalIdAttributeId = secondaryAttribute.Id,
            AttributeValues = [new ConnectedSystemObjectAttributeValue { Attribute = secondaryAttribute, StringValue = secondaryValue }]
        };
    }

    #region Finds match, returns status

    [Test]
    public async Task GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync_PendingProvisioningCso_ReturnsMatchWithStatusAsync()
    {
        await using var seed = NewContext();
        var (system, type, secondaryAttribute) = await SeedSystemAndTypeAsync(seed);
        var cso = CreateCso(system, type, secondaryAttribute, "CN=John,DC=test");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync(
            system.Id, type.Id, secondaryAttribute.Id, ["CN=John,DC=test"]);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Value, Is.EqualTo("CN=John,DC=test"));
        Assert.That(result[0].ConnectedSystemObjectId, Is.EqualTo(cso.Id));
        Assert.That(result[0].Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning));
    }

    [Test]
    public async Task GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync_NormalStatusCso_ReturnsMatchWithStatusAsync()
    {
        await using var seed = NewContext();
        var (system, type, secondaryAttribute) = await SeedSystemAndTypeAsync(seed);
        var cso = CreateCso(system, type, secondaryAttribute, "CN=John,DC=test", status: ConnectedSystemObjectStatus.Normal);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync(
            system.Id, type.Id, secondaryAttribute.Id, ["CN=John,DC=test"]);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal),
            "the query must report the real status; deciding what counts as a match is the caller's job");
    }

    #endregion

    #region Case sensitivity: exact equality

    [Test]
    public async Task GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync_CaseDiffers_DoesNotMatchAsync()
    {
        await using var seed = NewContext();
        var (system, type, secondaryAttribute) = await SeedSystemAndTypeAsync(seed);
        var cso = CreateCso(system, type, secondaryAttribute, "CN=John,DC=test");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync(
            system.Id, type.Id, secondaryAttribute.Id, ["cn=john,dc=test"]);

        Assert.That(result, Is.Empty, "a value differing only in case must not match: this is exact, case-sensitive equality, matching the single-object method");
    }

    #endregion

    #region Attribute id pinning (index use on Postgres, and matching semantics)

    /// <summary>
    /// Orchestrator-flagged performance fix: without pinning a known attribute id in the query's
    /// attribute-value join, Postgres cannot use IX_ConnectedSystemObjectAttributeValues_AttributeId_StringValue
    /// (the only attribute-id filter otherwise available, cso."SecondaryExternalIdAttributeId", is
    /// per-row and only known after the join to "ConnectedSystemObjects") - measured 1,011 ms for 500
    /// values against a live 100k-CSO database, vs 45 ms once pinned. Pinning it is not merely a
    /// query hint: it changes the query's matching semantics too. A CSO whose OWN
    /// SecondaryExternalIdAttributeId is a DIFFERENT attribute of the same type (e.g. one an
    /// administrator retargeted away from) must not match here, even holding the identical string
    /// value, because the import value being looked up was itself read from the type's CURRENT
    /// secondary external id attribute.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync_CsoSecondaryAttributeDiffersFromQueriedAttribute_IsExcludedAsync()
    {
        await using var seed = NewContext();
        var (system, type, secondaryAttribute) = await SeedSystemAndTypeAsync(seed);

        // A second Text attribute on the same type, standing in for an OLDER secondary external id
        // attribute an administrator has since retargeted away from (the type's current one is
        // "secondaryAttribute" above, seeded by SeedSystemAndTypeAsync).
        var oldSecondaryAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "oldDistinguishedName", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        seed.Add(oldSecondaryAttribute);
        await seed.SaveChangesAsync();

        // A CSO still configured against the OLD attribute, holding the SAME string value the
        // current attribute would use.
        var cso = new ConnectedSystemObject
        {
            Type = type,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            ExternalIdAttributeId = oldSecondaryAttribute.Id,
            SecondaryExternalIdAttributeId = oldSecondaryAttribute.Id,
            AttributeValues = [new ConnectedSystemObjectAttributeValue { Attribute = oldSecondaryAttribute, StringValue = "CN=John,DC=test" }]
        };
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Query using the type's CURRENT secondary attribute id, not the CSO's own (older) one.
        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync(
            system.Id, type.Id, secondaryAttribute.Id, ["CN=John,DC=test"]);

        Assert.That(result, Is.Empty,
            "a CSO whose configured secondary external id attribute differs from the one being queried must not match, even holding the same value");
    }

    #endregion

    #region Scoping: Connected System and object type

    [Test]
    public async Task GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync_OtherConnectedSystem_IsExcludedAsync()
    {
        await using var seed = NewContext();
        var (system, type, secondaryAttribute) = await SeedSystemAndTypeAsync(seed);
        var (otherSystem, otherType, otherSecondaryAttribute) = await SeedSystemAndTypeAsync(seed, systemName: "Other System");

        var cso = CreateCso(otherSystem, otherType, otherSecondaryAttribute, "CN=John,DC=test");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync(
            system.Id, type.Id, secondaryAttribute.Id, ["CN=John,DC=test"]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync_OtherObjectType_IsExcludedAsync()
    {
        await using var seed = NewContext();
        var (system, type, secondaryAttribute) = await SeedSystemAndTypeAsync(seed);

        var otherType = new ConnectedSystemObjectType { Name = "GROUP", ConnectedSystem = system, Selected = true };
        var otherSecondaryAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = secondaryAttribute.Name, ConnectedSystemObjectType = otherType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, IsSecondaryExternalId = true, Selected = true
        };
        otherType.Attributes.Add(otherSecondaryAttribute);
        seed.Add(otherType);
        await seed.SaveChangesAsync();

        var cso = CreateCso(system, otherType, otherSecondaryAttribute, "CN=John,DC=test");
        seed.Add(cso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync(
            system.Id, type.Id, secondaryAttribute.Id, ["CN=John,DC=test"]);

        Assert.That(result, Is.Empty);
    }

    #endregion

    #region Multiple rows for a duplicated value

    [Test]
    public async Task GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync_DuplicateValueAcrossTwoCsos_ReturnsBothRowsAsync()
    {
        await using var seed = NewContext();
        var (system, type, secondaryAttribute) = await SeedSystemAndTypeAsync(seed);
        var first = CreateCso(system, type, secondaryAttribute, "CN=Duplicate,DC=test");
        var second = CreateCso(system, type, secondaryAttribute, "CN=Duplicate,DC=test");
        seed.AddRange(first, second);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync(
            system.Id, type.Id, secondaryAttribute.Id, ["CN=Duplicate,DC=test"]);

        Assert.That(result, Has.Count.EqualTo(2),
            "an ambiguous value must return every matching row so the caller can leave it uncovered rather than pick a winner");
        Assert.That(result.Select(r => r.ConnectedSystemObjectId), Is.EquivalentTo(new[] { first.Id, second.Id }));
    }

    #endregion

    #region Multiple values in one call

    [Test]
    public async Task GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync_MultipleValues_ReturnsOneRowPerMatchAsync()
    {
        await using var seed = NewContext();
        var (system, type, secondaryAttribute) = await SeedSystemAndTypeAsync(seed);
        var csoA = CreateCso(system, type, secondaryAttribute, "CN=A,DC=test");
        var csoB = CreateCso(system, type, secondaryAttribute, "CN=B,DC=test");
        seed.AddRange(csoA, csoB);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync(
            system.Id, type.Id, secondaryAttribute.Id, ["CN=A,DC=test", "CN=B,DC=test", "CN=Nonexistent,DC=test"]);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Single(r => r.Value == "CN=A,DC=test").ConnectedSystemObjectId, Is.EqualTo(csoA.Id));
        Assert.That(result.Single(r => r.Value == "CN=B,DC=test").ConnectedSystemObjectId, Is.EqualTo(csoB.Id));
    }

    #endregion

    #region Empty input

    [Test]
    public async Task GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync_EmptyValues_ReturnsEmptyWithoutQueryingAsync()
    {
        await using var seed = NewContext();
        var (system, type, secondaryAttribute) = await SeedSystemAndTypeAsync(seed);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsBySecondaryExternalIdValuesAsync(
            system.Id, type.Id, secondaryAttribute.Id, []);

        Assert.That(result, Is.Empty);
    }

    #endregion
}
