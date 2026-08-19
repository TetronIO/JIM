// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Import reference resolution's database fallback must match values through the anchor attribute's own data
/// type (#1285). Reference values arrive as strings whatever the anchor's type; before #1285 the fallback
/// compared StringValue only, so a reference to a Guid- or int-anchored object could never resolve through it.
/// </summary>
/// <remarks>
/// <para>
/// Runs against real PostgreSQL because the typed matching lives in the EF query translation: the in-memory
/// suite exercises a hand-written mirror of it, which proves nothing about what the database is asked.
/// </para>
/// <para>
/// Objects are seeded directly (attribute values placed by their declared data type) because the question
/// under test is the query, not the writer; the writer's column placement is covered by the sibling
/// external id fixtures.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ReferenceFallbackTypedMatchingDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL reference fallback tests.");

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
    public async Task GetConnectedSystemObjectsByAttributeValuesAsync_AGuidAnchor_MatchesHoweverTheRequestIsCasedAsync()
    {
        var anchorValue = Guid.NewGuid();
        var seeded = await SeedSystemAsync(AttributeDataType.Guid, av => av.GuidValue = anchorValue);
        var requestedValue = anchorValue.ToString().ToUpperInvariant();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsByAttributeValuesAsync(
            seeded.SystemId, seeded.AnchorAttributeId, [requestedValue]);

        Assert.That(result.TryGetValue(requestedValue, out var match) && match != null, Is.True,
            "A Guid-anchored object must be found by its rendered value, keyed so the caller can probe with what it asked for.");
    }

    [Test]
    public async Task GetConnectedSystemObjectsByAttributeValuesAsync_AnIntAnchor_MatchesTheRenderedValueAsync()
    {
        var seeded = await SeedSystemAsync(AttributeDataType.Number, av => av.IntValue = 42);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsByAttributeValuesAsync(
            seeded.SystemId, seeded.AnchorAttributeId, ["42"]);

        Assert.That(result.ContainsKey("42"), Is.True,
            "An int-anchored object must be found by its rendered value; before #1285 only StringValue was compared and this returned nothing.");
    }

    [Test]
    public async Task GetConnectedSystemObjectsByAttributeValuesAsync_ATextAnchor_StillMatchesCaseInsensitivelyAsync()
    {
        var seeded = await SeedSystemAsync(AttributeDataType.Text, av => av.StringValue = "Employee-007");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsByAttributeValuesAsync(
            seeded.SystemId, seeded.AnchorAttributeId, ["EMPLOYEE-007"]);

        Assert.That(result.ContainsKey("employee-007"), Is.True,
            "Text matching behaviour is unchanged: case-insensitive, exactly as before #1285.");
    }

    [Test]
    public async Task GetConnectedSystemObjectsByAttributeValuesAsync_AValueNobodyHolds_ReturnsNothingAsync()
    {
        var seeded = await SeedSystemAsync(AttributeDataType.Number, av => av.IntValue = 42);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsByAttributeValuesAsync(
            seeded.SystemId, seeded.AnchorAttributeId, ["43", "not-a-number"]);

        Assert.That(result, Is.Empty);
    }

    #region seeding

    private sealed record SeededSystem(int SystemId, int AnchorAttributeId);

    /// <summary>
    /// Seeds one Connected System with one Object Type whose anchor has the given data type, and one
    /// Connected System Object carrying the supplied anchor value.
    /// </summary>
    private async Task<SeededSystem> SeedSystemAsync(AttributeDataType anchorType, Action<ConnectedSystemObjectAttributeValue> setAnchorValue)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = connectedSystem, Selected = true };
        var anchorAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "anchor",
            ConnectedSystemObjectType = objectType,
            Type = anchorType,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true,
            IsExternalId = true
        };
        objectType.Attributes.Add(anchorAttribute);
        seed.AddRange(connectorDefinition, connectedSystem, objectType);
        await seed.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            ConnectedSystemId = connectedSystem.Id,
            TypeId = objectType.Id,
            ExternalIdAttributeId = anchorAttribute.Id
        };
        var anchorValue = new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = cso,
            AttributeId = anchorAttribute.Id
        };
        setAnchorValue(anchorValue);
        cso.AttributeValues.Add(anchorValue);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        return new SeededSystem(connectedSystem.Id, anchorAttribute.Id);
    }

    #endregion
}
