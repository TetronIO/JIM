// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of export matching while a target object holds the rule's attribute only under another
/// data type (#2323), on both the per-object lookup and the page's prefetch. The attribute is a Number now; one account
/// still holds its value in the Text column, as a schema refresh leaves it until an import reads it again. The
/// candidate SQL compares only the Number column, so it cannot see that account; export matching must then refuse to
/// work the rule out rather than report no match and provision a duplicate. The in-memory provider cannot prove that
/// the rows read back from the database carry the columns the held check reads, so only a real database run does.
/// </summary>
/// <remarks>Seeds on one context and reads on another, as <see cref="ExportMatchCandidateDatabaseTests"/> does.</remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ExportMatchHeldUnderAnotherDataTypeDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL export matching tests.");

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

    private sealed record Seeded(ConnectedSystem System, ConnectedSystemObjectType Type, ConnectedSystemObjectTypeAttribute Attribute,
        Guid HeldId, Guid CurrentId);

    /// <summary>
    /// A target whose Number attribute "employeeNumber" is held by one account only as Text "1001" (when
    /// <paramref name="withHeld"/>), and by another as the Number 2002.
    /// </summary>
    private async Task<Seeded> SeedAsync(bool withHeld)
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid()}", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeNumber", ConnectedSystemObjectType = type, Type = AttributeDataType.Number,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        type.Attributes.Add(attribute);
        seed.AddRange(connectorDefinition, system, type);
        await seed.SaveChangesAsync();

        ConnectedSystemObject Account(ConnectedSystemObjectAttributeValue value) => new()
        {
            Type = type, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined, ExternalIdAttributeId = attribute.Id, AttributeValues = [value]
        };

        var current = Account(new ConnectedSystemObjectAttributeValue { Attribute = attribute, IntValue = 2002 });
        seed.Add(current);
        ConnectedSystemObject? held = null;
        if (withHeld)
        {
            held = Account(new ConnectedSystemObjectAttributeValue { Attribute = attribute, StringValue = "1001" });
            seed.Add(held);
        }

        await seed.SaveChangesAsync();
        return new Seeded(system, type, attribute, held?.Id ?? Guid.Empty, current.Id);
    }

    private static (MetaverseObject Mvo, ObjectMatchingRule Rule, SyncRule ExportRule) Arrange(Seeded seeded, int employeeNumber)
    {
        var mvType = new MetaverseObjectType { Id = 9001, Name = "Person" };
        var mvAttribute = new MetaverseAttribute { Id = 9002, Name = "Employee Number", Type = AttributeDataType.Number };
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvType,
            AttributeValues = [new MetaverseObjectAttributeValue { Attribute = mvAttribute, AttributeId = mvAttribute.Id, IntValue = employeeNumber }]
        };
        var rule = new ObjectMatchingRule
        {
            Id = 9003,
            Order = 0,
            TargetMetaverseAttribute = mvAttribute,
            TargetMetaverseAttributeId = mvAttribute.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = seeded.Attribute, ConnectedSystemAttributeId = seeded.Attribute.Id }]
        };

        // The Connected System's default mode reads the Object Type's shared rules, which is where the prefetch finds it.
        seeded.Type.ObjectMatchingRules.Clear();
        seeded.Type.ObjectMatchingRules.Add(rule);
        var exportRule = new SyncRule
        {
            Id = 9004,
            Name = "Directory Export",
            Direction = SyncRuleDirection.Export,
            ProvisionToConnectedSystem = true,
            ConnectedSystemId = seeded.System.Id,
            ConnectedSystem = seeded.System,
            ConnectedSystemObjectType = seeded.Type,
            MetaverseObjectType = mvType,
            ObjectMatchingRules = [rule]
        };
        return (mvo, rule, exportRule);
    }

    /// <summary>
    /// Export matching as export evaluation runs it: the page's prefetch where <paramref name="prefetch"/>, else the
    /// per-object lookup; either way with the run's held objects.
    /// </summary>
    private async Task<ExportMatch?> MatchAsync(Seeded seeded, int employeeNumber, bool prefetch)
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var jim = new JimApplication(repository, syncRepository: repository.Sync);
        var (mvo, rule, exportRule) = Arrange(seeded, employeeNumber);
        var cache = new ExportEvaluationCache(
            new Dictionary<int, List<SyncRule>> { [mvo.Type!.Id] = [exportRule] },
            [],
            Array.Empty<ConnectedSystemObjectAttributeValue>().ToLookup(av => (Guid.Empty, 0)),
            [seeded.System.Id]);

        if (prefetch)
        {
            await new SyncServer(jim).PrefetchExportMatchCandidatesForPageAsync(cache, [mvo]);
            Assert.That(cache.ExportMatchCandidates!.IsCovered(mvo.Id, exportRule.Id), Is.True, "the prefetch covers the pair");
        }

        return await jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, seeded.System, seeded.Type, [rule], cache.ExportMatchCandidates, cache.ExpressionMatchIndexes, cache.ExportMatchHeldObjects);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FindMatchingConnectedSystemObjectAsync_OnlyAccountHeldUnderAnotherDataType_FailsAsync(bool prefetch)
    {
        var seeded = await SeedAsync(withHeld: true);

        var ex = Assert.ThrowsAsync<ObjectMatchingRuleFailedException>(() => MatchAsync(seeded, 1001, prefetch));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain("1 existing Connected System Object in 'Directory'"));
            Assert.That(ex.Message, Does.Contain(seeded.HeldId.ToString()));
            Assert.That(ex.Message, Does.Contain("is held as Text, not as its Number type"));
        }

        // The account was never compared across types, and is still unjoined.
        await using var ctx = NewContext();
        Assert.That((await ctx.ConnectedSystemObjects.SingleAsync(c => c.Id == seeded.HeldId)).MetaverseObjectId, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FindMatchingConnectedSystemObjectAsync_AccountHoldsTheCurrentTypeWhileAnotherIsHeld_MatchesItAsync(bool prefetch)
    {
        var seeded = await SeedAsync(withHeld: true);

        var match = await MatchAsync(seeded, 2002, prefetch);

        Assert.That(match?.ConnectedSystemObject.Id, Is.EqualTo(seeded.CurrentId));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FindMatchingConnectedSystemObjectAsync_NoCandidateAndNoneHeld_ReturnsNullSoTheIdentityIsProvisionedAsync(bool prefetch)
    {
        var seeded = await SeedAsync(withHeld: false);

        var match = await MatchAsync(seeded, 3003, prefetch);

        Assert.That(match, Is.Null);
    }
}
