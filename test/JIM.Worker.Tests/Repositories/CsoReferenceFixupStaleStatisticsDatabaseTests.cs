// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL regression for the Connected System Object reference fix-up under statistics that predate its data
/// (#2706): an import writes a Connected System's objects and their references, some held by identifier alone because
/// the object they name was not known yet, and the tables' statistics were last gathered while the system had no
/// objects and nothing was outstanding.
/// </summary>
/// <remarks>
/// The statistics are made stale the way a run leaves them, as in
/// <see cref="ChangeRecordReferenceFixupStaleStatisticsDatabaseTests"/>: another system's objects are written and
/// analysed, and only then are the fixed-up system's objects and their outstanding references written, with no
/// analysis after. The fix-up runs under a statement timeout far above what any of its statements needs and far below
/// what a plan pairing every outstanding value with every object takes. Opt-in via the <c>JIM_TEST_RESET_*</c>
/// environment variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class CsoReferenceFixupStaleStatisticsDatabaseTests
{
    private const int ObjectsPerSystem = 6000;
    private const int ReferencesPerObject = 2;
    private const int OutstandingReferences = ObjectsPerSystem * ReferencesPerObject;
    private const int StatementTimeoutSeconds = 5;

    // Every table the fix-up reads, analysed together before the fixed-up system's data arrives.
    private static readonly string[] FixupTables = ["ConnectedSystemObjects", "ConnectedSystemObjectAttributeValues", "ConnectedSystemAttributes"];

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL Connected System Object reference fix-up stale statistics tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext(_connectionString);
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    private static JimDbContext NewContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [Test]
    public async Task FixupCrossBatchReferenceIdsAsync_ThousandsOutstandingUnderStatisticsTakenBeforeThem_ResolvesThemAllWithinTheTimeoutAsync()
    {
        var schema = await SeedSchemaAsync();
        await WriteObjectsAsync(schema.Source, "uid=user{0},ou=People,dc=yellowstone,dc=local", referenceDnFormat: null);
        await AnalyseFixupTablesAsync();
        // Lower case, so each match goes through the case-insensitive comparison as directory DNs do.
        await WriteObjectsAsync(schema.Target, "CN=User{0},OU=People,DC=glitterband,DC=local", "cn=user{0},ou=people,dc=glitterband,dc=local");

        // The bound is the server's own statement timeout, per statement, so a faulty plan fails with the error a run
        // would see (57014) rather than leaving the test to wait it out.
        var bounded = new NpgsqlConnectionStringBuilder(_connectionString) { Options = $"-c statement_timeout={StatementTimeoutSeconds}s" };
        var stopwatch = Stopwatch.StartNew();
        int resolved;
        await using (var ctx = NewContext(bounded.ConnectionString))
            resolved = await new PostgresDataRepository(ctx).Sync.FixupCrossBatchReferenceIdsAsync(schema.Target.SystemId);
        stopwatch.Stop();
        TestContext.Out.WriteLine($"Resolved {resolved:N0} outstanding references in {stopwatch.ElapsedMilliseconds:N0} ms.");

        await using var verify = NewContext(_connectionString);
        var stillOutstanding = await verify.ConnectedSystemObjectAttributeValues
            .CountAsync(av => av.UnresolvedReferenceValue != null && av.ReferenceValueId == null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.EqualTo(OutstandingReferences), "every outstanding reference names an object in the system");
            Assert.That(stillOutstanding, Is.Zero, "a resolved reference holds its object's id");
        }
    }

    #region seeding

    private sealed record SystemSchema(int SystemId, int TypeId, int DnId, int DescriptionId, int MemberId);

    private sealed record Schema(SystemSchema Source, SystemSchema Target);

    /// <summary>
    /// Two systems, each with people whose DN is their Secondary External ID and a multi-valued Reference attribute
    /// naming other people.
    /// </summary>
    private async Task<Schema> SeedSchemaAsync()
    {
        await using var seed = NewContext(_connectionString);
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var source = new ConnectedSystem { Name = "Yellowstone", ConnectorDefinition = connectorDefinition };
        var target = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var sourceType = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = source, Selected = true };
        var targetType = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = target, Selected = true };
        var sourceDn = Attribute(sourceType, "uid", AttributeDataType.Text, secondaryExternalId: true);
        var sourceDescription = Attribute(sourceType, "description", AttributeDataType.Text);
        var sourceMember = Attribute(sourceType, "member", AttributeDataType.Reference);
        var targetDn = Attribute(targetType, "distinguishedName", AttributeDataType.Text, secondaryExternalId: true);
        var targetDescription = Attribute(targetType, "description", AttributeDataType.Text);
        var targetMember = Attribute(targetType, "member", AttributeDataType.Reference);
        seed.AddRange(connectorDefinition, source, target, sourceType, targetType);
        await seed.SaveChangesAsync();

        return new Schema(
            new SystemSchema(source.Id, sourceType.Id, sourceDn.Id, sourceDescription.Id, sourceMember.Id),
            new SystemSchema(target.Id, targetType.Id, targetDn.Id, targetDescription.Id, targetMember.Id));
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType type, string name, AttributeDataType dataType, bool secondaryExternalId = false)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = name, ConnectedSystemObjectType = type, Type = dataType,
            AttributePlurality = dataType == AttributeDataType.Reference ? AttributePlurality.MultiValued : AttributePlurality.SingleValued,
            Selected = true, IsSecondaryExternalId = secondaryExternalId
        };
        type.Attributes.Add(attribute);
        return attribute;
    }

    /// <summary>
    /// People in the given system, each with a DN in its Secondary External ID and a second value, and, when a
    /// reference format is given, references to other people held by DN alone, through the import's own create writer.
    /// </summary>
    private async Task WriteObjectsAsync(SystemSchema system, string dnFormat, string? referenceDnFormat)
    {
        var objects = Enumerable.Range(1, ObjectsPerSystem).Select(n =>
        {
            var person = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(), ConnectedSystemId = system.SystemId, TypeId = system.TypeId, ExternalIdAttributeId = system.DnId,
                SecondaryExternalIdAttributeId = system.DnId, Status = ConnectedSystemObjectStatus.Normal
            };
            person.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = system.DnId, StringValue = string.Format(dnFormat, n) });
            person.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = system.DescriptionId, StringValue = $"Person {n}" });
            if (referenceDnFormat != null)
            {
                for (var r = 1; r <= ReferencesPerObject; r++)
                {
                    var named = (n * 7 + r) % ObjectsPerSystem + 1;
                    person.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                    {
                        Id = Guid.NewGuid(), AttributeId = system.MemberId, UnresolvedReferenceValue = string.Format(referenceDnFormat, named)
                    });
                }
            }
            return person;
        }).ToList();

        // In batches below the writer's parallel threshold (fifty objects per core, and at least two cores' worth), so
        // every batch is written on this context's own connection: the parallel path builds its connection string from
        // the environment, which other fixtures in the run point at a host that does not exist.
        await using var ctx = NewContext(_connectionString);
        var repository = new PostgresDataRepository(ctx);
        foreach (var batch in objects.Chunk(100))
            await repository.Sync.CreateConnectedSystemObjectsAsync([.. batch]);
    }

    /// <summary>
    /// Gathers the fix-up tables' statistics now, as autovacuum does while a run's earlier stages write them.
    /// </summary>
    private async Task AnalyseFixupTablesAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"ANALYZE {string.Join(", ", FixupTables.Select(t => $"\"{t}\""))};", connection);
        await command.ExecuteNonQueryAsync();
    }

    #endregion
}
