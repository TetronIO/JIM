// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
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
/// Real-PostgreSQL verification of <c>ConnectedSystemRepository.GetConnectedSystemObjectsByIdsAsync</c>, the
/// per-page CSO hydration query called from <c>SyncImportTaskProcessor.HydrateCsoPageAsync</c>. Before the fix,
/// the query loaded schema (Object Type + its Attributes) via a split query whose SQL joined the Attributes
/// table back to <c>ConnectedSystemObjects</c>, so it returned (CSOs in the page x attributes per type) rows of
/// identical schema data: about 5.4 million redundant rows at 100,000 CSOs. The fix loads the schema once per
/// call for the distinct object types referenced, then wires each CSO's <c>Type</c> and each attribute value's
/// <c>Attribute</c> to the shared instances by hand, so the returned object graph is unchanged in shape.
/// </summary>
/// <remarks>
/// Seeding pattern mirrors <see cref="ExportMatchCandidateDatabaseTests"/>: every entity a test writes
/// (Connector Definition, Connected System, Object Types, Attributes, Connected System Objects) is created and
/// saved through ONE <c>seed</c> context per test, never split across separately-disposed contexts. EF's
/// <c>Add()</c>/<c>AddRange()</c> walks the whole reachable entity graph and (re-)inserts every navigation it
/// finds untracked (`src/CLAUDE.md` &gt; "DbSet.Add Walks the Graph"); a detached, already-persisted parent
/// obtained from a disposed context is exactly such an untracked navigation, so adding a child that still
/// references it re-attempts the parent's insert and fails with a duplicate key on its primary key. Keeping the
/// whole seed on one context means a later <c>Add()</c> finds the parent already tracked (Unchanged) and stops
/// the walk there. Only the read-only call under test uses a second, fresh context, matching how the repository
/// is actually used in production (a query, never a write).
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class CsoBatchHydrationDatabaseTests
{
    private string _connectionString = null!;
    private readonly CommandCountingInterceptor _interceptor = new();

    private JimDbContext NewContext(bool countCommands = false)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

        if (countCommands)
            options.AddInterceptors(_interceptor);

        return new JimDbContext(options.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL CSO batch hydration tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        _interceptor.Reset();
    }

    /// <summary>
    /// Builds and saves a Connected System and a single Object Type carrying one attribute of the given
    /// data type, on the caller's own context. The caller must use the SAME context for any further entity
    /// creation that references the returned entities (see the class remarks).
    /// </summary>
    private static async Task<(ConnectedSystem System, ConnectedSystemObjectType Type, ConnectedSystemObjectTypeAttribute Attribute)> SeedSystemAndTypeAsync(
        JimDbContext seed, string attributeName = "employeeId", string systemName = "Yellowstone Target")
    {
        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid()}", BuiltIn = true };
        var system = new ConnectedSystem { Name = systemName, ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = attributeName,
            ConnectedSystemObjectType = csType,
            Type = AttributeDataType.Text,
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
        ConnectedSystemObjectTypeAttribute externalIdAttribute,
        string externalIdValue)
    {
        var av = new ConnectedSystemObjectAttributeValue { Attribute = externalIdAttribute, StringValue = externalIdValue };
        return new ConnectedSystemObject
        {
            Type = type,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = externalIdAttribute.Id,
            AttributeValues = [av]
        };
    }

    #region Characterisation: object graph invariants (guards the refactor; passes before and after)

    /// <summary>
    /// Proves the invariants the refactor must preserve exactly, across CSOs of two different Object Types:
    /// every CSO's <c>Type</c> is populated with its full <c>Attributes</c> collection; CSOs of the same type
    /// share ONE <c>Type</c> instance; every attribute value's <c>Attribute</c> is populated and is the SAME
    /// instance as the matching element of its CSO's <c>Type.Attributes</c>; the back-navigations EF's
    /// Include-based fixup gives today (an attribute's reference back to its Object Type, and an attribute
    /// value's reference back to its Connected System Object) are still populated; and nothing ends up tracked.
    /// This test is expected to pass unchanged before and after the fix; it is here to guard the refactor, not
    /// to demonstrate the bug.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsByIdsAsync_CsosOfTwoObjectTypes_PreservesSchemaGraphInvariantsAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid()}", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };

        var userType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var userExternalIdAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeId", ConnectedSystemObjectType = userType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var userDisplayNameAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = userType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        userType.Attributes.Add(userExternalIdAttribute);
        userType.Attributes.Add(userDisplayNameAttribute);

        var groupType = new ConnectedSystemObjectType { Name = "GROUP", ConnectedSystem = system, Selected = true };
        var groupExternalIdAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "groupId", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        groupType.Attributes.Add(groupExternalIdAttribute);

        seed.AddRange(connectorDefinition, system, userType, groupType);
        await seed.SaveChangesAsync();

        var user1 = CreateCso(system, userType, userExternalIdAttribute, "E1");
        user1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = userDisplayNameAttribute, StringValue = "Alice" });
        var user2 = CreateCso(system, userType, userExternalIdAttribute, "E2");
        user2.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = userDisplayNameAttribute, StringValue = "Bob" });
        var group1 = CreateCso(system, groupType, groupExternalIdAttribute, "G1");

        seed.AddRange(user1, user2, group1);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsByIdsAsync(
            system.Id, new[] { user1.Id, user2.Id, group1.Id });

        Assert.That(result, Has.Count.EqualTo(3));

        var resultUser1 = result.Single(c => c.Id == user1.Id);
        var resultUser2 = result.Single(c => c.Id == user2.Id);
        var resultGroup1 = result.Single(c => c.Id == group1.Id);

        using (Assert.EnterMultipleScope())
        {
            // Every CSO's Type is populated with its full Attributes collection.
            Assert.That(resultUser1.Type, Is.Not.Null);
            Assert.That(resultUser1.Type.Attributes, Has.Count.EqualTo(2));
            Assert.That(resultGroup1.Type, Is.Not.Null);
            Assert.That(resultGroup1.Type.Attributes, Has.Count.EqualTo(1));

            // CSOs of the same type share ONE Type instance; CSOs of a different type do not.
            Assert.That(resultUser2.Type, Is.SameAs(resultUser1.Type));
            Assert.That(resultGroup1.Type, Is.Not.SameAs(resultUser1.Type));

            foreach (var cso in result)
            {
                foreach (var av in cso.AttributeValues)
                {
                    Assert.That(av.Attribute, Is.Not.Null, $"attribute value {av.Id} must have its Attribute populated");
                    var expectedAttribute = cso.Type.Attributes.Single(a => a.Id == av.AttributeId);

                    // Every attribute value's Attribute is the SAME instance as the element of
                    // cso.Type.Attributes with that id.
                    Assert.That(av.Attribute, Is.SameAs(expectedAttribute),
                        "the attribute value's Attribute must be the same instance as the matching element of its CSO's Type.Attributes");

                    // Back-navigation EF's Include-based fixup gives today: the attribute's own
                    // reference back to its Connected System Object Type.
                    Assert.That(av.Attribute.ConnectedSystemObjectType, Is.SameAs(cso.Type),
                        "the attribute's back-reference to its Connected System Object Type must be populated, matching today's Include-based fixup");

                    // Back-navigation from the attribute value to its parent Connected System Object.
                    Assert.That(av.ConnectedSystemObject, Is.SameAs(cso),
                        "the attribute value's back-reference to its Connected System Object must be populated, matching today's Include-based fixup");
                }
            }

            // Nothing is tracked by the context afterwards; this is a no-tracking read.
            Assert.That(ctx.ChangeTracker.Entries().Any(), Is.False);
        }
    }

    #endregion

    #region Query shape: schema must be loaded once per call, not once per CSO

    /// <summary>
    /// The bug this fixes: the schema (Object Type + Attributes) was loaded by a split query whose SQL joined
    /// the Attributes table back through the Object Type to <c>ConnectedSystemObjects</c>, so it returned one
    /// copy of the type's attributes per matching CSO row rather than once per distinct type. This test fails
    /// before the fix (the join is present) and passes after (the schema query touches only
    /// <c>ConnectedSystemObjectTypes</c> and <c>ConnectedSystemAttributes</c>, never <c>ConnectedSystemObjects</c>).
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsByIdsAsync_HydratingAttributes_DoesNotJoinAttributesBackToConnectedSystemObjectsAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed);
        var cso1 = CreateCso(system, type, attribute, "E1");
        var cso2 = CreateCso(system, type, attribute, "E2");
        seed.AddRange(cso1, cso2);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext(countCommands: true);
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsByIdsAsync(
            system.Id, new[] { cso1.Id, cso2.Id });

        Assert.That(result, Has.Count.EqualTo(2));

        var offendingCommands = _interceptor.CommandTexts
            .Where(sql => sql.Contains("\"ConnectedSystemAttributes\"", StringComparison.Ordinal)
                       && sql.Contains("\"ConnectedSystemObjects\"", StringComparison.Ordinal))
            .ToList();

        Assert.That(offendingCommands, Is.Empty,
            "no query hydrating attribute schema should join back to ConnectedSystemObjects (attributes must be " +
            $"loaded once per call, not once per CSO); offending command(s):{Environment.NewLine}" +
            string.Join(Environment.NewLine + "---" + Environment.NewLine, offendingCommands));
    }

    /// <summary>
    /// A more direct measure of the same bug than the SQL-shape check above: the number of distinct Type
    /// instances (and therefore Attribute rows) the call materialises. Before the fix, identity resolution
    /// still collapses these to one shared instance in the RESULT graph, so this alone would not turn red; the
    /// real symptom is redundant rows fetched from the database, which the query-shape test above catches. This
    /// test instead pins down the shared-instance behaviour so a future change cannot silently start
    /// materialising a Type instance per CSO again.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsByIdsAsync_ManyCsosOfOneType_ShareOneTypeInstanceAsync()
    {
        await using var seed = NewContext();
        var (system, type, attribute) = await SeedSystemAndTypeAsync(seed);

        const int csoCount = 10;
        var csos = Enumerable.Range(0, csoCount).Select(i => CreateCso(system, type, attribute, $"E{i}")).ToList();
        seed.AddRange(csos);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetConnectedSystemObjectsByIdsAsync(
            system.Id, csos.Select(c => c.Id));

        Assert.That(result, Has.Count.EqualTo(csoCount));

        // Every CSO shares the SAME Type instance with the SAME single-element Attributes collection.
        var distinctTypeInstances = result.Select(c => c.Type).Distinct().ToList();
        Assert.That(distinctTypeInstances, Has.Count.EqualTo(1));
        Assert.That(distinctTypeInstances[0].Attributes, Has.Count.EqualTo(1),
            "the type's Attributes collection must contain exactly the one attribute, once, not once per CSO sharing the type");
    }

    #endregion

    /// <summary>
    /// Counts the commands the provider executes and records their text, which is how the query-shape
    /// assertion above is made.
    /// </summary>
    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commandTexts = new();

        public IReadOnlyList<string> CommandTexts => _commandTexts;

        public void Reset() => _commandTexts.Clear();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            _commandTexts.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commandTexts.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
