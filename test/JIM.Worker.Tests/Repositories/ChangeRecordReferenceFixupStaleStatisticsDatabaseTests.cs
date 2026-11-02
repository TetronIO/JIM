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
using InitiatorType = JIM.Models.Activities.ActivityInitiatorType;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL regression for the change record reference fix-up under statistics that predate its data (#2690):
/// an initial export writes thousands of marked references to objects it provisions in the same run, and the tables'
/// statistics, last gathered while the target Connected System had no objects and nothing was marked, say there are
/// none of either. Scenario 032 at Medium failed this way: the planner drove a nested loop from the marked values,
/// estimated at one row, and re-ran the whole target side for each of them, so the candidate step ran into the 300 s
/// statement timeout (<c>57014</c>) during the export.
/// </summary>
/// <remarks>
/// The statistics are made stale the way a run leaves them: the source system's objects and ordinary change history
/// are written and analysed, as autovacuum does during the import and synchronisation, and only then are the target
/// system's objects and the export's marked references written, with no analysis after. A freshly migrated database
/// that was never analysed does not reproduce it, because the planner then sizes tables from their pages. The fix-up
/// runs under a statement timeout far above what any of its statements needs and far below what either faulty plan
/// takes. Measured on PostgreSQL 18.6 at this volume: matching the marked values to the target side in one join took
/// 64 s (72 million pairs filtered), and applying the resolutions with a predicate on the mark took 25 s (the batch
/// rescanned once per marked value); the whole fix-up now takes about half a second, its slowest statement about
/// 0.2 s. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ChangeRecordReferenceFixupStaleStatisticsDatabaseTests
{
    private const int ObjectsPerSystem = 6000;
    private const int MarkedReferences = 12000;
    private const int StatementTimeoutSeconds = 5;

    // Every table the fix-up reads, analysed together before the target system's data arrives.
    private static readonly string[] FixupTables =
    [
        "ConnectedSystemObjectChangeAttributeValues", "ConnectedSystemObjectChangeAttributes", "ConnectedSystemObjectChanges",
        "ConnectedSystemObjects", "ConnectedSystemObjectAttributeValues", "ConnectedSystemAttributes"
    ];

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL change record fix-up stale statistics tests.");
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
    public async Task FixupCrossBatchChangeRecordReferenceIdsAsync_ThousandsMarkedUnderStatisticsTakenBeforeThem_ResolvesThemAllWithinTheTimeoutAsync()
    {
        var schema = await SeedSchemaAsync();
        await WriteObjectsAsync(schema.SourceSystemId, schema.SourceTypeId, schema.SourceDnId, schema.SourceOtherId, "uid=user{0},ou=People,dc=yellowstone,dc=local");
        await WriteOrdinaryChangeHistoryAsync(schema);
        await AnalyseFixupTablesAsync();
        await WriteObjectsAsync(schema.TargetSystemId, schema.TargetTypeId, schema.TargetDnId, schema.TargetOtherId, "CN=User{0},OU=People,DC=glitterband,DC=local");
        await WriteMarkedReferencesAsync(schema);

        // The bound is the server's own statement timeout, per statement, so a faulty plan fails with the error the run
        // saw (57014) rather than leaving the test to wait it out.
        var bounded = new NpgsqlConnectionStringBuilder(_connectionString) { Options = $"-c statement_timeout={StatementTimeoutSeconds}s" };
        var stopwatch = Stopwatch.StartNew();
        int resolved;
        await using (var ctx = NewContext(bounded.ConnectionString))
            resolved = await new PostgresDataRepository(ctx).Sync.FixupCrossBatchChangeRecordReferenceIdsAsync(schema.TargetSystemId);
        stopwatch.Stop();
        TestContext.Out.WriteLine($"Resolved {resolved:N0} marked references in {stopwatch.ElapsedMilliseconds:N0} ms.");

        await using var verify = NewContext(_connectionString);
        var stillMarked = await verify.ConnectedSystemObjectChangeAttributeValues.CountAsync(v => v.IsUnresolvedReference);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolved, Is.EqualTo(MarkedReferences), "every marked reference names a target object");
            Assert.That(stillMarked, Is.Zero, "a resolved reference loses its mark");
        }
    }

    #region seeding

    private sealed record Schema(
        int SourceSystemId, int SourceTypeId, int SourceDnId, int SourceOtherId,
        int TargetSystemId, int TargetTypeId, int TargetDnId, int TargetOtherId,
        ConnectedSystemObjectTypeAttribute SourceDescription, ConnectedSystemObjectTypeAttribute TargetMember);

    /// <summary>
    /// A source system and a target system, each with people whose DN is their Secondary External ID; the target's
    /// people have a Reference attribute naming other people.
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
        var targetDn = Attribute(targetType, "distinguishedName", AttributeDataType.Text, secondaryExternalId: true);
        var targetDescription = Attribute(targetType, "description", AttributeDataType.Text);
        var targetMember = Attribute(targetType, "member", AttributeDataType.Reference);
        seed.AddRange(connectorDefinition, source, target, sourceType, targetType);
        await seed.SaveChangesAsync();

        return new Schema(source.Id, sourceType.Id, sourceDn.Id, sourceDescription.Id,
            target.Id, targetType.Id, targetDn.Id, targetDescription.Id, sourceDescription, targetMember);
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType type, string name, AttributeDataType dataType, bool secondaryExternalId = false)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = AttributePlurality.SingleValued,
            Selected = true, IsSecondaryExternalId = secondaryExternalId
        };
        type.Attributes.Add(attribute);
        return attribute;
    }

    /// <summary>
    /// People in the given system, each with a DN in its Secondary External ID and a second value, through the
    /// import's own create writer.
    /// </summary>
    private async Task WriteObjectsAsync(int systemId, int typeId, int dnAttributeId, int otherAttributeId, string dnFormat)
    {
        var objects = Enumerable.Range(1, ObjectsPerSystem).Select(n =>
        {
            var person = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(), ConnectedSystemId = systemId, TypeId = typeId, ExternalIdAttributeId = dnAttributeId,
                SecondaryExternalIdAttributeId = dnAttributeId, Status = ConnectedSystemObjectStatus.Normal
            };
            person.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = dnAttributeId, StringValue = string.Format(dnFormat, n) });
            person.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = otherAttributeId, StringValue = $"Person {n}" });
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
    /// The import's change history for the source system: ordinary Text values, none of them marked.
    /// </summary>
    private async Task WriteOrdinaryChangeHistoryAsync(Schema schema)
    {
        var changes = Enumerable.Range(1, ObjectsPerSystem).Select(n =>
        {
            var (change, attributeChange) = NewChange(schema.SourceSystemId, schema.SourceDescription, ObjectChangeType.Added);
            attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, ValueChangeType.Add, $"Person {n}"));
            return change;
        }).ToList();

        await using var ctx = NewContext(_connectionString);
        await new PostgresDataRepository(ctx).Sync.PersistCsoChangesAsync(changes);
    }

    /// <summary>
    /// The export's change records for the target system: one per exported person, its members named by DN alone, so
    /// the writer marks every one of them.
    /// </summary>
    private async Task WriteMarkedReferencesAsync(Schema schema)
    {
        var changes = Enumerable.Range(1, ObjectsPerSystem)
            .Select(_ => NewChange(schema.TargetSystemId, schema.TargetMember, ObjectChangeType.Exported))
            .ToList();
        for (var n = 0; n < MarkedReferences; n++)
        {
            var attributeChange = changes[n % ObjectsPerSystem].AttributeChange;
            // Lower case, so each match goes through the case-insensitive comparison as directory DNs do.
            var dn = $"cn=user{n * 7 % ObjectsPerSystem + 1},ou=people,dc=glitterband,dc=local";
            attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, ValueChangeType.Add, dn));
        }

        await using var ctx = NewContext(_connectionString);
        await new PostgresDataRepository(ctx).Sync.PersistCsoChangesAsync([.. changes.Select(c => c.Change)]);
    }

    private static (ConnectedSystemObjectChange Change, ConnectedSystemObjectChangeAttribute AttributeChange) NewChange(
        int systemId, ConnectedSystemObjectTypeAttribute attribute, ObjectChangeType changeType)
    {
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = systemId, ChangeTime = DateTime.UtcNow, ChangeType = changeType, InitiatedByType = InitiatorType.System
        };
        var attributeChange = new ConnectedSystemObjectChangeAttribute
        {
            Attribute = attribute, AttributeName = attribute.Name, AttributeType = attribute.Type, ConnectedSystemChange = change
        };
        change.AttributeChanges.Add(attributeChange);
        return (change, attributeChange);
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
