// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
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
/// Real-PostgreSQL characterisation of <c>SyncRepository.CreatePendingExportsAsync</c>: every column
/// of a newly staged Pending Export and its attribute value changes must round-trip exactly, and a
/// failure part-way through must leave no row behind. Written before adding COPY binary writers for
/// this path (it previously had no COPY path at all, unlike its CSO/MVO/RPEI siblings), so this
/// fixture proves the switch from parameterised multi-row INSERT preserves observable behaviour.
/// Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportBulkCreateColumnRoundTripDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export bulk create round-trip tests.");

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
        int SystemId, int TextAttrId, Guid ConnectedSystemObjectId, Guid ResolvedReferenceCsoId, Guid SourceMvoId,
        int ProvisioningSyncRuleId);

    /// <summary>
    /// Seeds a Connected System, an attribute for the attribute value change, two Connected System
    /// Objects (one to be the Pending Export's own <c>ConnectedSystemObjectId</c> for an update/delete
    /// against an existing object, one to be the <c>ResolvedReferenceCsoId</c> a reference change
    /// resolved to), a Metaverse Object for <c>SourceMetaverseObjectId</c>, and a Synchronisation Rule
    /// for <c>ProvisioningSyncRuleId</c>.
    /// </summary>
    private async Task<Seeded> SeedGraphAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband LDAP", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var textAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(textAttr);

        var targetCso = new ConnectedSystemObject { Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal };
        var resolvedReferenceCso = new ConnectedSystemObject { Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal };
        var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var sourceMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
        var syncRule = new SyncRule
        {
            Name = "HR Provisioning", ConnectedSystem = system, ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvoType, Direction = SyncRuleDirection.Export
        };

        seed.AddRange(connectorDefinition, system, csType, targetCso, resolvedReferenceCso, mvoType, sourceMvo, syncRule);
        await seed.SaveChangesAsync();

        return new Seeded(system.Id, textAttr.Id, targetCso.Id, resolvedReferenceCso.Id, sourceMvo.Id, syncRule.Id);
    }

    [Test]
    public async Task CreatePendingExportsAsync_EveryColumnPopulated_RoundTripsExactlyAsync()
    {
        var s = await SeedGraphAsync();

        // PostgreSQL's timestamptz has microsecond precision; .NET's DateTime ticks are 100ns (7 digits),
        // so an untruncated DateTime.UtcNow occasionally carries a sub-microsecond remainder that a
        // round-trip through the database silently drops. Truncate so the comparison is exact.
        var lastAttemptedAt = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(-10));
        var nextRetryAt = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(5));
        var createdAt = TruncateToMicroseconds(DateTime.UtcNow.AddHours(-1));
        var lastExportedAt = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(-2));
        var sourceMvoId = s.SourceMvoId;
        var provisioningSyncRuleId = s.ProvisioningSyncRuleId;
        var queueingItemId = Guid.NewGuid();
        var dateTimeValue = TruncateToMicroseconds(DateTime.UtcNow.AddDays(-3));
        var byteValue = new byte[] { 4, 5, 6 };
        var guidValue = Guid.NewGuid();

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = s.SystemId,
            ConnectedSystemObjectId = s.ConnectedSystemObjectId,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.ExportNotConfirmed,
            ErrorCount = 2,
            MaxRetries = 5,
            LastAttemptedAt = lastAttemptedAt,
            NextRetryAt = nextRetryAt,
            LastErrorMessage = "the connector rejected the change",
            LastErrorStackTrace = "at Foo.Bar()",
            SourceMetaverseObjectId = sourceMvoId,
            HasUnresolvedReferences = true,
            CreatedAt = createdAt,
            ProvisioningSyncRuleId = provisioningSyncRuleId,
            QueuedByRunProfileExecutionItemId = queueingItemId
        };
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = s.TextAttrId,
            StringValue = "Jo Bloggs",
            DateTimeValue = dateTimeValue,
            IntValue = 42,
            LongValue = 9999999999L,
            DecimalValue = 3.14159m,
            ByteValue = byteValue,
            GuidValue = guidValue,
            BoolValue = true,
            UnresolvedReferenceValue = "cn=unresolved,dc=corp,dc=example",
            ChangeType = PendingExportAttributeChangeType.Add,
            Status = PendingExportAttributeChangeStatus.ExportedNotConfirmed,
            ExportAttemptCount = 1,
            LastExportedAt = lastExportedAt,
            LastImportedValue = "the value the connector actually returned",
            ResolvedReferenceCsoId = s.ResolvedReferenceCsoId,
            SyncRuleId = 42,
            SyncRuleName = "HR Export"
        });
        // A second change on the same export whose every nullable field is null, so the round-trip
        // proves the writer does not confuse "no value" with a spurious default.
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = s.TextAttrId,
            ChangeType = PendingExportAttributeChangeType.Remove,
            Status = PendingExportAttributeChangeStatus.Pending
        });

        // Act
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            await repository.Sync.CreatePendingExportsAsync([pendingExport]);
        }

        // Assert: read back on a fresh context
        await using var readContext = NewContext();
        var storedExport = await readContext.PendingExports.AsNoTracking().SingleAsync(pe => pe.Id == pendingExport.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storedExport.ConnectedSystemId, Is.EqualTo(s.SystemId));
            Assert.That(storedExport.ConnectedSystemObjectId, Is.EqualTo(s.ConnectedSystemObjectId));
            Assert.That(storedExport.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
            Assert.That(storedExport.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed));
            Assert.That(storedExport.ErrorCount, Is.EqualTo(2));
            Assert.That(storedExport.MaxRetries, Is.EqualTo(5));
            Assert.That(storedExport.LastAttemptedAt, Is.EqualTo(lastAttemptedAt));
            Assert.That(storedExport.NextRetryAt, Is.EqualTo(nextRetryAt));
            Assert.That(storedExport.LastErrorMessage, Is.EqualTo("the connector rejected the change"));
            Assert.That(storedExport.LastErrorStackTrace, Is.EqualTo("at Foo.Bar()"));
            Assert.That(storedExport.SourceMetaverseObjectId, Is.EqualTo(sourceMvoId));
            Assert.That(storedExport.HasUnresolvedReferences, Is.True);
            Assert.That(storedExport.CreatedAt, Is.EqualTo(createdAt));
            Assert.That(storedExport.ProvisioningSyncRuleId, Is.EqualTo(provisioningSyncRuleId));
            Assert.That(storedExport.QueuedByRunProfileExecutionItemId, Is.EqualTo(queueingItemId));
        }

        var storedChanges = await readContext.PendingExportAttributeValueChanges
            .AsNoTracking()
            .Where(avc => EF.Property<Guid?>(avc, "PendingExportId") == pendingExport.Id)
            .ToListAsync();
        Assert.That(storedChanges, Has.Count.EqualTo(2));

        using (Assert.EnterMultipleScope())
        {
            var populated = storedChanges.Single(c => c.ChangeType == PendingExportAttributeChangeType.Add);
            Assert.That(populated.AttributeId, Is.EqualTo(s.TextAttrId));
            Assert.That(populated.StringValue, Is.EqualTo("Jo Bloggs"));
            Assert.That(populated.DateTimeValue, Is.EqualTo(dateTimeValue));
            Assert.That(populated.IntValue, Is.EqualTo(42));
            Assert.That(populated.LongValue, Is.EqualTo(9999999999L));
            Assert.That(populated.DecimalValue, Is.EqualTo(3.14159m));
            Assert.That(populated.ByteValue, Is.EqualTo(byteValue));
            Assert.That(populated.GuidValue, Is.EqualTo(guidValue));
            Assert.That(populated.BoolValue, Is.True);
            Assert.That(populated.UnresolvedReferenceValue, Is.EqualTo("cn=unresolved,dc=corp,dc=example"));
            Assert.That(populated.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));
            Assert.That(populated.ExportAttemptCount, Is.EqualTo(1));
            Assert.That(populated.LastExportedAt, Is.EqualTo(lastExportedAt));
            Assert.That(populated.LastImportedValue, Is.EqualTo("the value the connector actually returned"));
            Assert.That(populated.ResolvedReferenceCsoId, Is.EqualTo(s.ResolvedReferenceCsoId));
            Assert.That(populated.SyncRuleId, Is.EqualTo(42));
            Assert.That(populated.SyncRuleName, Is.EqualTo("HR Export"));

            var empty = storedChanges.Single(c => c.ChangeType == PendingExportAttributeChangeType.Remove);
            Assert.That(empty.StringValue, Is.Null);
            Assert.That(empty.DateTimeValue, Is.Null);
            Assert.That(empty.IntValue, Is.Null);
            Assert.That(empty.LongValue, Is.Null);
            Assert.That(empty.DecimalValue, Is.Null);
            Assert.That(empty.ByteValue, Is.Null);
            Assert.That(empty.GuidValue, Is.Null);
            Assert.That(empty.BoolValue, Is.Null);
            Assert.That(empty.UnresolvedReferenceValue, Is.Null);
            Assert.That(empty.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending));
            Assert.That(empty.ExportAttemptCount, Is.EqualTo(0));
            Assert.That(empty.LastExportedAt, Is.Null);
            Assert.That(empty.LastImportedValue, Is.Null);
            Assert.That(empty.ResolvedReferenceCsoId, Is.Null);
            Assert.That(empty.SyncRuleId, Is.Null);
            Assert.That(empty.SyncRuleName, Is.Null);
        }
    }

    /// <summary>
    /// A Pending Export whose attribute value change references a nonexistent AttributeId must fail
    /// the whole staging call: <c>CreatePendingExportsAsync</c> writes the parent row then the
    /// attribute value change rows in one transaction, so a foreign key violation on the child write
    /// must roll back the parent row already written earlier in the same transaction.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_AttributeValueChangeReferencesNonexistentAttribute_LeavesNoRowsBehindAsync()
    {
        var s = await SeedGraphAsync();
        const int nonexistentAttributeId = int.MaxValue;

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = s.SystemId,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = nonexistentAttributeId, StringValue = "poisoned",
            ChangeType = PendingExportAttributeChangeType.Add, Status = PendingExportAttributeChangeStatus.Pending
        });

        await using var writeContext = NewContext();
        var repository = new PostgresDataRepository(writeContext);
        Assert.That(async () => await repository.Sync.CreatePendingExportsAsync([pendingExport]),
            Throws.Exception, "a foreign key violation on the attribute value change write must propagate, not be swallowed");

        await using var verify = NewContext();
        var survivingCount = await verify.PendingExports.CountAsync(pe => pe.Id == pendingExport.Id);
        Assert.That(survivingCount, Is.Zero,
            "the Pending Export must not survive: the attribute value change failure must roll back the whole transaction, including the parent row already written earlier in it");
    }

    /// <summary>
    /// Truncates to PostgreSQL's timestamptz microsecond precision (1 microsecond = 10 ticks), so a
    /// value built from <see cref="DateTime.UtcNow"/> round-trips byte-for-byte through the database.
    /// </summary>
    private static DateTime TruncateToMicroseconds(DateTime value) => new(value.Ticks / 10 * 10, value.Kind);
}
