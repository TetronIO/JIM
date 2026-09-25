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
/// Real-PostgreSQL characterisation of <c>SyncRepository.CreateMetaverseObjectsBulkAsync</c>'s
/// single-connection branch (page-sized batches, below the parallel COPY threshold): every column
/// of a newly created Metaverse Object and its attribute values must round-trip exactly, and a
/// failure part-way through the batch must leave no row behind. Written before converting that
/// branch from parameterised multi-row INSERT to COPY binary import on the EF connection's own
/// transaction, so this fixture proves the write path's observable behaviour is unchanged by the
/// switch. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MvoBulkCreateColumnRoundTripDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL MVO bulk create round-trip tests.");

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

    private sealed record Seeded(
        MetaverseObjectType MvoType, int TextAttrId, int NumberAttrId, int LongNumberAttrId, int DecimalAttrId,
        int BinaryAttrId, int GuidAttrId, int BoolAttrId, int DateAttrId, int ReferenceAttrId,
        int ContributingSystemId, int ContributingSyncRuleId, Guid UnresolvedReferenceCsoId);

    /// <summary>
    /// Seeds a Metaverse Object Type with one attribute per value carrier, plus a Connected System,
    /// Synchronisation Rule and Connected System Object so <c>ContributedBySystemId</c>,
    /// <c>ContributedBySyncRuleId</c> and <c>UnresolvedReferenceValueId</c> all resolve real foreign
    /// keys. All entities go through ONE context (see CsoBulkCreateColumnRoundTripDatabaseTests'
    /// remarks on why).
    /// </summary>
    private async Task<Seeded> SeedGraphAsync()
    {
        await using var seed = NewContext();

        var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var textAttr = new MetaverseAttribute { Name = "displayName", Type = AttributeDataType.Text };
        var numberAttr = new MetaverseAttribute { Name = "employeeNumber", Type = AttributeDataType.Number };
        var longNumberAttr = new MetaverseAttribute { Name = "accountExpires", Type = AttributeDataType.LongNumber };
        var decimalAttr = new MetaverseAttribute { Name = "fte", Type = AttributeDataType.Decimal };
        var binaryAttr = new MetaverseAttribute { Name = "thumbnailPhoto", Type = AttributeDataType.Binary };
        var guidAttr = new MetaverseAttribute { Name = "correlationId", Type = AttributeDataType.Guid };
        var boolAttr = new MetaverseAttribute { Name = "enabled", Type = AttributeDataType.Boolean };
        var dateAttr = new MetaverseAttribute { Name = "whenChanged", Type = AttributeDataType.DateTime };
        var referenceAttr = new MetaverseAttribute { Name = "manager", Type = AttributeDataType.Reference };

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone HR", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var syncRule = new SyncRule
        {
            Name = "HR Import", ConnectedSystem = system, ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvoType, Direction = SyncRuleDirection.Import
        };
        var unresolvedReferenceCso = new ConnectedSystemObject
        {
            Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal
        };

        seed.AddRange(mvoType, textAttr, numberAttr, longNumberAttr, decimalAttr, binaryAttr, guidAttr, boolAttr,
            dateAttr, referenceAttr, connectorDefinition, system, csType, syncRule, unresolvedReferenceCso);
        await seed.SaveChangesAsync();

        return new Seeded(mvoType, textAttr.Id, numberAttr.Id, longNumberAttr.Id, decimalAttr.Id, binaryAttr.Id,
            guidAttr.Id, boolAttr.Id, dateAttr.Id, referenceAttr.Id, system.Id, syncRule.Id, unresolvedReferenceCso.Id);
    }

    [Test]
    public async Task CreateMetaverseObjectsBulkAsync_PageSizedBatchEveryColumnPopulated_RoundTripsExactlyAsync()
    {
        var s = await SeedGraphAsync();

        // PostgreSQL's timestamptz has microsecond precision; .NET's DateTime ticks are 100ns (7 digits),
        // so an untruncated DateTime.UtcNow occasionally carries a sub-microsecond remainder that a
        // round-trip through the database silently drops. Truncate so the comparison is exact.
        var lastConnectorDisconnected = TruncateToMicroseconds(DateTime.UtcNow.AddDays(-3));
        var lastScopeEvaluatedAt = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(-20));
        var lastUpdated = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(-2));
        var whenChanged = TruncateToMicroseconds(DateTime.UtcNow.AddDays(-1));
        var byteValue = new byte[] { 9, 8, 7, 6 };
        var guidValue = Guid.NewGuid();
        var deletionInitiatedById = Guid.NewGuid();

        // A second MVO in the same batch, referenced by the first's Reference attribute value: the
        // single-connection path inserts all MVO rows before any attribute value, so a same-batch
        // forward reference resolves without cross-page fixup.
        var referencedMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = s.MvoType, Created = DateTime.UtcNow };

        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = s.MvoType,
            Created = DateTime.UtcNow,
            LastUpdated = lastUpdated,
            Status = MetaverseObjectStatus.Normal,
            Origin = MetaverseObjectOrigin.Internal,
            LastConnectorDisconnectedDate = lastConnectorDisconnected,
            DeletionInitiatedByType = JIM.Models.Activities.ActivityInitiatorType.User,
            DeletionInitiatedById = deletionInitiatedById,
            DeletionInitiatedByName = "Jo Admin",
            DeletionTriggeredBySystemId = s.ContributingSystemId,
            DeletionTriggeredBySystemName = "Yellowstone HR",
            DeletionPolicySnapshotJson = """{"deletionRule":"WhenAuthoritativeSourceDisconnected"}""",
            CachedDisplayName = "Jo Bloggs",
            ScopeReviewPending = true,
            LastScopeEvaluatedAt = lastScopeEvaluatedAt
        };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.TextAttrId, StringValue = "Jo Bloggs", ContributedBySystemId = s.ContributingSystemId, ContributedBySyncRuleId = s.ContributingSyncRuleId });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.NumberAttrId, IntValue = 42 });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.LongNumberAttrId, LongValue = 9999999999L });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.DecimalAttrId, DecimalValue = 3.14159m });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.BinaryAttrId, ByteValue = byteValue });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.GuidAttrId, GuidValue = guidValue });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.BoolAttrId, BoolValue = true });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.DateAttrId, DateTimeValue = whenChanged });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.ReferenceAttrId, ReferenceValueId = referencedMvo.Id });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.ReferenceAttrId, UnresolvedReferenceValueId = s.UnresolvedReferenceCsoId });
        // An asserted-null marker row: all value columns null, NullValue true.
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.TextAttrId, NullValue = true });

        // Act: persist through the single-connection create path (2 MVOs is well below the parallel threshold)
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            await repository.Sync.CreateMetaverseObjectsAsync([referencedMvo, mvo]);
        }

        // Assert: read back on a fresh context
        await using var readContext = NewContext();
        var storedMvo = await readContext.MetaverseObjects.AsNoTracking().SingleAsync(m => m.Id == mvo.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storedMvo.LastUpdated, Is.EqualTo(lastUpdated));
            Assert.That(storedMvo.Status, Is.EqualTo(MetaverseObjectStatus.Normal));
            Assert.That(storedMvo.Origin, Is.EqualTo(MetaverseObjectOrigin.Internal));
            Assert.That(storedMvo.LastConnectorDisconnectedDate, Is.EqualTo(lastConnectorDisconnected));
            Assert.That(storedMvo.DeletionInitiatedByType, Is.EqualTo(JIM.Models.Activities.ActivityInitiatorType.User));
            Assert.That(storedMvo.DeletionInitiatedById, Is.EqualTo(deletionInitiatedById));
            Assert.That(storedMvo.DeletionInitiatedByName, Is.EqualTo("Jo Admin"));
            Assert.That(storedMvo.DeletionTriggeredBySystemId, Is.EqualTo(s.ContributingSystemId));
            Assert.That(storedMvo.DeletionTriggeredBySystemName, Is.EqualTo("Yellowstone HR"));
            Assert.That(storedMvo.DeletionPolicySnapshotJson, Is.EqualTo("""{"deletionRule":"WhenAuthoritativeSourceDisconnected"}"""));
            Assert.That(storedMvo.CachedDisplayName, Is.EqualTo("Jo Bloggs"));
            Assert.That(storedMvo.ScopeReviewPending, Is.True);
            Assert.That(storedMvo.LastScopeEvaluatedAt, Is.EqualTo(lastScopeEvaluatedAt));
        }

        var storedValues = await readContext.MetaverseObjectAttributeValues
            .AsNoTracking()
            .Where(av => EF.Property<Guid>(av, "MetaverseObjectId") == mvo.Id)
            .ToListAsync();
        Assert.That(storedValues, Has.Count.EqualTo(11));

        using (Assert.EnterMultipleScope())
        {
            var textValue = storedValues.Single(v => v.AttributeId == s.TextAttrId && !v.NullValue);
            Assert.That(textValue.StringValue, Is.EqualTo("Jo Bloggs"));
            Assert.That(textValue.ContributedBySystemId, Is.EqualTo(s.ContributingSystemId));
            Assert.That(textValue.ContributedBySyncRuleId, Is.EqualTo(s.ContributingSyncRuleId));
            Assert.That(storedValues.Single(v => v.AttributeId == s.NumberAttrId).IntValue, Is.EqualTo(42));
            Assert.That(storedValues.Single(v => v.AttributeId == s.LongNumberAttrId).LongValue, Is.EqualTo(9999999999L));
            Assert.That(storedValues.Single(v => v.AttributeId == s.DecimalAttrId).DecimalValue, Is.EqualTo(3.14159m));
            Assert.That(storedValues.Single(v => v.AttributeId == s.BinaryAttrId).ByteValue, Is.EqualTo(byteValue));
            Assert.That(storedValues.Single(v => v.AttributeId == s.GuidAttrId).GuidValue, Is.EqualTo(guidValue));
            Assert.That(storedValues.Single(v => v.AttributeId == s.BoolAttrId).BoolValue, Is.True);
            Assert.That(storedValues.Single(v => v.AttributeId == s.DateAttrId).DateTimeValue, Is.EqualTo(whenChanged));
            var resolvedRef = storedValues.Single(v => v.AttributeId == s.ReferenceAttrId && v.ReferenceValueId != null);
            Assert.That(resolvedRef.ReferenceValueId, Is.EqualTo(referencedMvo.Id),
                "A same-batch forward reference must resolve because MVO rows commit before attribute values on the single-connection path");
            var unresolvedRef = storedValues.Single(v => v.AttributeId == s.ReferenceAttrId && v.ReferenceValueId == null);
            Assert.That(unresolvedRef.UnresolvedReferenceValueId, Is.EqualTo(s.UnresolvedReferenceCsoId));
            var nullMarker = storedValues.Single(v => v.NullValue);
            Assert.That(nullMarker.AttributeId, Is.EqualTo(s.TextAttrId));
            Assert.That(nullMarker.StringValue, Is.Null, "an asserted-null marker row must carry no payload");
        }
    }

    /// <summary>
    /// A batch whose second MVO carries an attribute value referencing a nonexistent AttributeId must
    /// fail the whole batch: the single-connection path writes all MVO rows then all attribute value
    /// rows in one transaction, so a foreign key violation on the attribute value write must roll back
    /// the MVO rows already written earlier in the same transaction.
    /// </summary>
    [Test]
    public async Task CreateMetaverseObjectsBulkAsync_AttributeValueReferencesNonexistentAttribute_LeavesNoRowsBehindAsync()
    {
        var s = await SeedGraphAsync();
        const int nonexistentAttributeId = int.MaxValue;

        var goodMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = s.MvoType, Created = DateTime.UtcNow };
        var badMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = s.MvoType, Created = DateTime.UtcNow };
        badMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = nonexistentAttributeId, StringValue = "poisoned"
        });

        await using var writeContext = NewContext();
        var repository = new PostgresDataRepository(writeContext);
        Assert.That(async () => await repository.Sync.CreateMetaverseObjectsAsync([goodMvo, badMvo]),
            Throws.Exception, "a foreign key violation on the attribute value write must propagate, not be swallowed");

        await using var verify = NewContext();
        var survivingCount = await verify.MetaverseObjects
            .CountAsync(m => m.Id == goodMvo.Id || m.Id == badMvo.Id);
        Assert.That(survivingCount, Is.Zero,
            "neither MVO must survive: the attribute value failure must roll back the whole single-connection transaction, including MVO rows already COPY'd earlier in it");
    }

    /// <summary>
    /// Truncates to PostgreSQL's timestamptz microsecond precision (1 microsecond = 10 ticks), so a
    /// value built from <see cref="DateTime.UtcNow"/> round-trips byte-for-byte through the database.
    /// </summary>
    private static DateTime TruncateToMicroseconds(DateTime value) => new(value.Ticks / 10 * 10, value.Kind);
}
