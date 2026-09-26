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
/// Real-PostgreSQL characterisation of <c>SyncRepository.CreateConnectedSystemObjectsAsync</c>'s
/// single-connection branch (page-sized batches, below the parallel COPY threshold): every column
/// of a newly created Connected System Object and its attribute values must round-trip exactly, and
/// a failure part-way through the batch must leave no row behind. Written before converting that
/// branch from parameterised multi-row INSERT to COPY binary import on the EF connection's own
/// transaction, so this fixture proves the write path's observable behaviour is unchanged by the
/// switch, not merely that it compiles. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class CsoBulkCreateColumnRoundTripDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL CSO bulk create round-trip tests.");

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
    }

    /// <summary>
    /// Seeds a Connected System with one attribute per value carrier plus external/secondary-external-id
    /// attributes, a Partition, and a Metaverse Object to join against. All entities go through ONE
    /// context so EF's identity map, not a second insert, is what a later Add() finds (see
    /// ExportMatchCandidateDatabaseTests' remarks on why: seeding across disposed contexts re-attempts
    /// the parent's insert and fails on its primary key).
    /// </summary>
    private async Task<Seeded> SeedGraphAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone HR", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var extIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = csType, Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var secondaryExtIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsSecondaryExternalId = true
        };
        var textAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var numberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeNumber", ConnectedSystemObjectType = csType, Type = AttributeDataType.Number,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var longNumberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "accountExpires", ConnectedSystemObjectType = csType, Type = AttributeDataType.LongNumber,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var decimalAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "fte", ConnectedSystemObjectType = csType, Type = AttributeDataType.Decimal,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var binaryAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "thumbnailPhoto", ConnectedSystemObjectType = csType, Type = AttributeDataType.Binary,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var guidAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "correlationId", ConnectedSystemObjectType = csType, Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var boolAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "enabled", ConnectedSystemObjectType = csType, Type = AttributeDataType.Boolean,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var dateAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "whenChanged", ConnectedSystemObjectType = csType, Type = AttributeDataType.DateTime,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var referenceAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "manager", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.AddRange([
            extIdAttr, secondaryExtIdAttr, textAttr, numberAttr, longNumberAttr, decimalAttr,
            binaryAttr, guidAttr, boolAttr, dateAttr, referenceAttr
        ]);

        var partition = new ConnectedSystemPartition
        {
            ConnectedSystem = system, Name = "DC=corp,DC=example", ExternalId = "partition-1", Selected = true
        };

        var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };

        seed.AddRange(connectorDefinition, system, csType, partition, mvoType, mvo);
        await seed.SaveChangesAsync();

        return new Seeded(system.Id, csType.Id, extIdAttr.Id, secondaryExtIdAttr.Id, textAttr.Id, numberAttr.Id,
            longNumberAttr.Id, decimalAttr.Id, binaryAttr.Id, guidAttr.Id, boolAttr.Id, dateAttr.Id,
            referenceAttr.Id, partition.Id, mvo.Id);
    }

    private sealed record Seeded(
        int SystemId, int TypeId, int ExtIdAttrId, int SecondaryExtIdAttrId, int TextAttrId, int NumberAttrId,
        int LongNumberAttrId, int DecimalAttrId, int BinaryAttrId, int GuidAttrId, int BoolAttrId, int DateAttrId,
        int ReferenceAttrId, int PartitionId, Guid MvoId);

    [Test]
    public async Task CreateConnectedSystemObjectsAsync_PageSizedBatchEveryColumnPopulated_RoundTripsExactlyAsync()
    {
        var s = await SeedGraphAsync();

        // PostgreSQL's timestamptz has microsecond precision; .NET's DateTime ticks are 100ns (7 digits),
        // so an untruncated DateTime.UtcNow occasionally carries a sub-microsecond remainder that a
        // round-trip through the database silently drops. Truncate so the comparison is exact.
        var whenChanged = TruncateToMicroseconds(DateTime.UtcNow.AddDays(-1));
        var dateJoined = TruncateToMicroseconds(DateTime.UtcNow.AddHours(-2));
        var lastScopeEvaluatedAt = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(-30));
        var lastUpdated = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(-5));
        var byteValue = new byte[] { 1, 2, 3, 4, 5 };
        var guidValue = Guid.NewGuid();

        // A second CSO in the same batch, referenced by the first's Reference attribute value: the
        // single-connection path inserts all CSO rows before any attribute value, on one connection
        // and one transaction, so a same-batch forward reference resolves without cross-batch fixup.
        var referencedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = s.SystemId,
            TypeId = s.TypeId,
            ExternalIdAttributeId = s.ExtIdAttrId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined,
            Created = DateTime.UtcNow
        };

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = s.SystemId,
            TypeId = s.TypeId,
            ExternalIdAttributeId = s.ExtIdAttrId,
            SecondaryExternalIdAttributeId = s.SecondaryExtIdAttrId,
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObjectId = s.MvoId,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            DateJoined = dateJoined,
            PartitionId = s.PartitionId,
            ScopeReviewPending = true,
            LastScopeEvaluatedAt = lastScopeEvaluatedAt,
            Created = DateTime.UtcNow,
            LastUpdated = lastUpdated
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.TextAttrId, StringValue = "Jo Bloggs" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.NumberAttrId, IntValue = 42 });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.LongNumberAttrId, LongValue = 9999999999L });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.DecimalAttrId, DecimalValue = 3.14159m });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.BinaryAttrId, ByteValue = byteValue });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.GuidAttrId, GuidValue = guidValue });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.BoolAttrId, BoolValue = true });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.DateAttrId, DateTimeValue = whenChanged });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.ReferenceAttrId, ReferenceValueId = referencedCso.Id });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.ReferenceAttrId, UnresolvedReferenceValue = "cn=unresolved,dc=corp,dc=example" });

        // Act: persist through the single-connection create path (2 CSOs is well below the parallel threshold)
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            await repository.Sync.CreateConnectedSystemObjectsAsync([referencedCso, cso]);
        }

        // Assert: read back on a fresh context
        await using var readContext = NewContext();
        var storedCso = await readContext.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == cso.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storedCso.ConnectedSystemId, Is.EqualTo(s.SystemId));
            Assert.That(storedCso.TypeId, Is.EqualTo(s.TypeId));
            Assert.That(storedCso.ExternalIdAttributeId, Is.EqualTo(s.ExtIdAttrId));
            Assert.That(storedCso.SecondaryExternalIdAttributeId, Is.EqualTo(s.SecondaryExtIdAttrId));
            Assert.That(storedCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(storedCso.MetaverseObjectId, Is.EqualTo(s.MvoId));
            Assert.That(storedCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined));
            Assert.That(storedCso.DateJoined, Is.EqualTo(dateJoined));
            Assert.That(storedCso.PartitionId, Is.EqualTo(s.PartitionId));
            Assert.That(storedCso.ScopeReviewPending, Is.True);
            Assert.That(storedCso.LastScopeEvaluatedAt, Is.EqualTo(lastScopeEvaluatedAt));
            Assert.That(storedCso.LastUpdated, Is.EqualTo(lastUpdated));
            Assert.That(storedCso.ImportStateHash, Is.Null, "SPEC-1082 D6: a newly created CSO must never carry a pre-stamped content hash");
            Assert.That(storedCso.ImportStateFingerprint, Is.Null, "SPEC-1082 D6: a newly created CSO must never carry a pre-stamped fingerprint");
        }

        var storedValues = await readContext.ConnectedSystemObjectAttributeValues
            .AsNoTracking()
            .Where(av => EF.Property<Guid>(av, "ConnectedSystemObjectId") == cso.Id)
            .ToListAsync();
        Assert.That(storedValues, Has.Count.EqualTo(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storedValues.Single(v => v.AttributeId == s.TextAttrId).StringValue, Is.EqualTo("Jo Bloggs"));
            Assert.That(storedValues.Single(v => v.AttributeId == s.NumberAttrId).IntValue, Is.EqualTo(42));
            Assert.That(storedValues.Single(v => v.AttributeId == s.LongNumberAttrId).LongValue, Is.EqualTo(9999999999L));
            Assert.That(storedValues.Single(v => v.AttributeId == s.DecimalAttrId).DecimalValue, Is.EqualTo(3.14159m));
            Assert.That(storedValues.Single(v => v.AttributeId == s.BinaryAttrId).ByteValue, Is.EqualTo(byteValue));
            Assert.That(storedValues.Single(v => v.AttributeId == s.GuidAttrId).GuidValue, Is.EqualTo(guidValue));
            Assert.That(storedValues.Single(v => v.AttributeId == s.BoolAttrId).BoolValue, Is.True);
            Assert.That(storedValues.Single(v => v.AttributeId == s.DateAttrId).DateTimeValue, Is.EqualTo(whenChanged));
            var resolvedRef = storedValues.Single(v => v.AttributeId == s.ReferenceAttrId && v.ReferenceValueId != null);
            Assert.That(resolvedRef.ReferenceValueId, Is.EqualTo(referencedCso.Id),
                "A same-batch forward reference must resolve because CSO rows commit before attribute values on the single-connection path");
            var unresolvedRef = storedValues.Single(v => v.AttributeId == s.ReferenceAttrId && v.ReferenceValueId == null);
            Assert.That(unresolvedRef.UnresolvedReferenceValue, Is.EqualTo("cn=unresolved,dc=corp,dc=example"));
        }
    }

    /// <summary>
    /// A batch whose second CSO carries an attribute value referencing a nonexistent AttributeId must
    /// fail the whole batch: the single-connection path writes all CSO rows then all attribute value
    /// rows in one transaction, so a foreign key violation on the attribute value write must roll back
    /// the CSO rows already written earlier in the same transaction. Synchronisation integrity requires
    /// this: a half-written batch (parent CSOs with no attribute values) is worse than none at all.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsAsync_AttributeValueReferencesNonexistentAttribute_LeavesNoRowsBehindAsync()
    {
        var s = await SeedGraphAsync();
        const int nonexistentAttributeId = int.MaxValue;

        var goodCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = s.SystemId,
            TypeId = s.TypeId,
            ExternalIdAttributeId = s.ExtIdAttrId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined,
            Created = DateTime.UtcNow
        };
        var badCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = s.SystemId,
            TypeId = s.TypeId,
            ExternalIdAttributeId = s.ExtIdAttrId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined,
            Created = DateTime.UtcNow
        };
        badCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = nonexistentAttributeId, StringValue = "poisoned"
        });

        await using var writeContext = NewContext();
        var repository = new PostgresDataRepository(writeContext);
        Assert.That(async () => await repository.Sync.CreateConnectedSystemObjectsAsync([goodCso, badCso]),
            Throws.Exception, "a foreign key violation on the attribute value write must propagate, not be swallowed");

        await using var verify = NewContext();
        var survivingCount = await verify.ConnectedSystemObjects
            .CountAsync(c => c.Id == goodCso.Id || c.Id == badCso.Id);
        Assert.That(survivingCount, Is.Zero,
            "neither CSO must survive: the attribute value failure must roll back the whole single-connection transaction, including CSO rows already COPY'd earlier in it");
    }

    /// <summary>
    /// Truncates to PostgreSQL's timestamptz microsecond precision (1 microsecond = 10 ticks), so a
    /// value built from <see cref="DateTime.UtcNow"/> round-trips byte-for-byte through the database.
    /// </summary>
    private static DateTime TruncateToMicroseconds(DateTime value) => new(value.Ticks / 10 * 10, value.Kind);
}
