// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// The repository lookups an import uses to find "the same object" by its External ID follow the anchor attribute's
/// case-exact declaration (#2051): ignoring case unless the attribute is case-exact. Also pins .NET's folding
/// (<see cref="IdentifierKey.For"/>) against PostgreSQL's <c>lower()</c>, since some lookups fold one side in .NET and
/// the other in SQL.
/// </summary>
/// <remarks>
/// Runs against real PostgreSQL: the folding under test is the database's own, which the in-memory suite cannot see.
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class CaseExactIdentifierLookupDatabaseTests
{
    private string _connectionString = null!;

    // Accented Latin, Turkish dotted capital I (which .NET's invariant lowering leaves alone and PostgreSQL does not),
    // German capital sharp s, Greek with a final sigma, and a titlecase digraph.
    private static readonly string[] NonAsciiValues = ["ÉMILE", "İSTANBUL", "STRAẞE", "ΣΊΣΥΦΟΣ", "ǅURO"];

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL case-exact identifier tests.");

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

    [TestCaseSource(nameof(NonAsciiValues))]
    public async Task IdentifierKeyFor_IgnoringCase_FoldsAsPostgresLowerDoesAsync(string value)
    {
        await using var ctx = NewContext();
        var lowered = await ctx.Database.SqlQueryRaw<string>("SELECT lower({0}) AS \"Value\"", value).SingleAsync();

        Assert.That(IdentifierKey.For(value, caseExact: false), Is.EqualTo(lowered),
            "A key folded in .NET must equal the database's lower() of the same value, or lookups that fold one side in each miss.");
    }

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_AnchorIgnoringCase_FindsTheObjectInAnotherCaseAsync()
    {
        var seeded = await SeedAsync(caseExact: false, "JBloggs");

        await using var ctx = NewContext();
        var found = await new PostgresDataRepository(ctx).ConnectedSystems
            .GetConnectedSystemObjectByAttributeAsync(seeded.SystemId, seeded.AnchorAttributeId, "jbloggs");

        Assert.That(found?.Id, Is.EqualTo(seeded.CsoIds[0]));
    }

    [Test]
    public async Task GetConnectedSystemObjectByAttributeAsync_CaseExactAnchor_FindsOnlyTheObjectInThatCaseAsync()
    {
        var seeded = await SeedAsync(caseExact: true, "JBloggs", "jbloggs");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).ConnectedSystems;
        var upper = await repository.GetConnectedSystemObjectByAttributeAsync(seeded.SystemId, seeded.AnchorAttributeId, "JBloggs");
        var lower = await repository.GetConnectedSystemObjectByAttributeAsync(seeded.SystemId, seeded.AnchorAttributeId, "jbloggs");
        var neither = await repository.GetConnectedSystemObjectByAttributeAsync(seeded.SystemId, seeded.AnchorAttributeId, "JBLOGGS");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(upper?.Id, Is.EqualTo(seeded.CsoIds[0]));
            Assert.That(lower?.Id, Is.EqualTo(seeded.CsoIds[1]));
            Assert.That(neither, Is.Null);
        }
    }

    [TestCaseSource(nameof(NonAsciiValues))]
    public async Task GetConnectedSystemObjectByAttributeAsync_NonAsciiAnchorIgnoringCase_FindsTheObjectByItsOwnValueAsync(string value)
    {
        var seeded = await SeedAsync(caseExact: false, value);

        await using var ctx = NewContext();
        var found = await new PostgresDataRepository(ctx).ConnectedSystems
            .GetConnectedSystemObjectByAttributeAsync(seeded.SystemId, seeded.AnchorAttributeId, value);

        Assert.That(found?.Id, Is.EqualTo(seeded.CsoIds[0]));
    }

    [Test]
    public async Task GetAllCsoImportStateLookupAsync_CaseExactAnchor_KeysEachObjectInItsOwnCaseAsync()
    {
        var seeded = await SeedAsync(caseExact: true, "JBloggs", "jbloggs");

        await using var ctx = NewContext();
        var lookup = await new PostgresDataRepository(ctx).ConnectedSystems.GetAllCsoImportStateLookupAsync(seeded.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lookup[CsoLookupKey.For(seeded.SystemId, seeded.AnchorAttributeId, "JBloggs")].CsoId, Is.EqualTo(seeded.CsoIds[0]));
            Assert.That(lookup[CsoLookupKey.For(seeded.SystemId, seeded.AnchorAttributeId, "jbloggs")].CsoId, Is.EqualTo(seeded.CsoIds[1]));
        }
    }

    [Test]
    public async Task GetAllCsoImportStateLookupAsync_AnchorIgnoringCase_KeysTheFoldedValueAsync()
    {
        var seeded = await SeedAsync(caseExact: false, "JBloggs");

        await using var ctx = NewContext();
        var lookup = await new PostgresDataRepository(ctx).ConnectedSystems.GetAllCsoImportStateLookupAsync(seeded.SystemId);

        Assert.That(lookup.ContainsKey(CsoLookupKey.For(seeded.SystemId, seeded.AnchorAttributeId, "jbloggs")), Is.True);
    }

    #region seeding

    private sealed record Seeded(int SystemId, int ObjectTypeId, int AnchorAttributeId, List<Guid> CsoIds);

    /// <summary>
    /// One Connected System, one Object Type whose Text anchor carries the given declaration, and one Connected System
    /// Object per anchor value, in the order given.
    /// </summary>
    private async Task<Seeded> SeedAsync(bool caseExact, params string[] anchorValues)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = connectedSystem, Selected = true };
        var anchorAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "accountId",
            ConnectedSystemObjectType = objectType,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true,
            IsExternalId = true,
            CaseExact = caseExact
        };
        objectType.Attributes.Add(anchorAttribute);
        seed.AddRange(connectorDefinition, connectedSystem, objectType);
        await seed.SaveChangesAsync();

        var csos = anchorValues.Select(value =>
        {
            var cso = new ConnectedSystemObject
            {
                ConnectedSystemId = connectedSystem.Id,
                TypeId = objectType.Id,
                ExternalIdAttributeId = anchorAttribute.Id,
                Status = ConnectedSystemObjectStatus.Normal
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                ConnectedSystemObject = cso,
                AttributeId = anchorAttribute.Id,
                StringValue = value
            });
            return cso;
        }).ToList();
        seed.AddRange(csos);
        await seed.SaveChangesAsync();

        return new Seeded(connectedSystem.Id, objectType.Id, anchorAttribute.Id, csos.Select(c => c.Id).ToList());
    }

    #endregion
}
