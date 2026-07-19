// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the batched cross-batch change record reference fixup.
/// </summary>
/// <remarks>
/// The Scale500k25kGroups integration run (2026-07-18) accumulated 6.5M unresolved change record
/// reference values across the sync and export stages; the previous single-statement UPDATE then
/// exceeded the 300s bulk command timeout during the confirming import and hard-failed the run.
/// The fixup now materialises the resolutions once and applies them in bounded batches, so each
/// statement stays well inside the timeout regardless of backlog size. These tests lock in the
/// rewritten SQL's semantics: correct resolution across batch boundaries, case-insensitive DN
/// matching, Connected System scoping, exclusion of non-reference values, termination despite
/// unresolvable rows, and idempotency. The timeout itself is only reproducible at multi-million-row
/// volume, so the at-scale proof lives in the integration suite; opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ChangeRecordReferenceFixupDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL change record reference fixup tests.");

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

    private sealed record FixupSeed(
        int TargetSystemId,
        Dictionary<string, Guid> CsoIdsByDn,
        List<Guid> ResolvableValueIds,
        List<Guid> UnresolvableValueIds,
        Guid NonReferenceValueId,
        Guid OtherSystemValueId);

    /// <summary>
    /// Seeds a target Connected System holding user CSOs whose secondary external ID attribute
    /// carries their DN, plus change records with reference values stored as DN strings and
    /// <c>ReferenceValueId</c> nulled, mirroring the state binary COPY persistence leaves for the
    /// fixup to resolve. Also seeds rows the fixup must NOT touch: unresolvable DNs, a text-typed
    /// change value whose string equals a real DN, and a matching value on another Connected System.
    /// </summary>
    private async Task<FixupSeed> SeedChangeRecordsWithUnresolvedReferencesAsync(int resolvableCount)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var targetSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var otherSystem = new ConnectedSystem { Name = "Yellowstone", ConnectorDefinition = connectorDefinition };

        var targetType = new ConnectedSystemObjectType { Name = "jimPerson", ConnectedSystem = targetSystem, Selected = true };
        var dnAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName", ConnectedSystemObjectType = targetType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsSecondaryExternalId = true
        };
        targetType.Attributes.Add(dnAttr);

        var otherType = new ConnectedSystemObjectType { Name = "jimPerson", ConnectedSystem = otherSystem, Selected = true };
        var otherDnAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName", ConnectedSystemObjectType = otherType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsSecondaryExternalId = true
        };
        otherType.Attributes.Add(otherDnAttr);

        seed.AddRange(connectorDefinition, targetSystem, otherSystem, targetType, otherType);
        await seed.SaveChangesAsync();

        // Target CSOs, DNs stored in canonical casing; change records will reference them lowercased
        // to prove the RFC 4514 case-insensitive match.
        var csoIdsByDn = new Dictionary<string, Guid>();
        for (var i = 0; i < resolvableCount; i++)
        {
            var dn = $"CN=User{i},OU=People,DC=glitterband,DC=local";
            var cso = new ConnectedSystemObject
            {
                Type = targetType,
                ConnectedSystem = targetSystem,
                Status = ConnectedSystemObjectStatus.Normal,
                ExternalIdAttributeId = dnAttr.Id
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = dnAttr.Id,
                StringValue = dn
            });
            seed.Add(cso);
            csoIdsByDn[dn] = cso.Id;
        }
        await seed.SaveChangesAsync();

        var change = new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Exported,
            InitiatedByType = ActivityInitiatorType.System
        };
        var memberChangeAttr = new ConnectedSystemObjectChangeAttribute
        {
            Id = Guid.NewGuid(),
            ConnectedSystemChange = change,
            AttributeName = "member",
            AttributeType = AttributeDataType.Reference
        };
        change.AttributeChanges.Add(memberChangeAttr);

        var resolvableValueIds = new List<Guid>();
        foreach (var dn in csoIdsByDn.Keys)
        {
            var value = new ConnectedSystemObjectChangeAttributeValue
            {
                Id = Guid.NewGuid(),
                ConnectedSystemObjectChangeAttribute = memberChangeAttr,
                ValueChangeType = ValueChangeType.Add,
                StringValue = dn.ToLowerInvariant()
            };
            memberChangeAttr.ValueChanges.Add(value);
            resolvableValueIds.Add(value.Id);
        }

        // Reference values whose DN matches no CSO; they must survive the fixup unresolved and
        // must not prevent loop termination.
        var unresolvableValueIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var value = new ConnectedSystemObjectChangeAttributeValue
            {
                Id = Guid.NewGuid(),
                ConnectedSystemObjectChangeAttribute = memberChangeAttr,
                ValueChangeType = ValueChangeType.Add,
                StringValue = $"CN=Ghost{i},OU=People,DC=glitterband,DC=local"
            };
            memberChangeAttr.ValueChanges.Add(value);
            unresolvableValueIds.Add(value.Id);
        }

        // A text-typed change value whose string happens to equal a real DN; the AttributeType
        // filter must exclude it.
        var descriptionChangeAttr = new ConnectedSystemObjectChangeAttribute
        {
            Id = Guid.NewGuid(),
            ConnectedSystemChange = change,
            AttributeName = "description",
            AttributeType = AttributeDataType.Text
        };
        var nonReferenceValue = new ConnectedSystemObjectChangeAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObjectChangeAttribute = descriptionChangeAttr,
            ValueChangeType = ValueChangeType.Add,
            StringValue = csoIdsByDn.Keys.First()
        };
        descriptionChangeAttr.ValueChanges.Add(nonReferenceValue);
        change.AttributeChanges.Add(descriptionChangeAttr);
        seed.Add(change);

        // A reference change value on ANOTHER Connected System whose DN matches a target CSO; the
        // Connected System scoping must exclude it.
        var otherChange = new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = otherSystem.Id,
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Exported,
            InitiatedByType = ActivityInitiatorType.System
        };
        var otherChangeAttr = new ConnectedSystemObjectChangeAttribute
        {
            Id = Guid.NewGuid(),
            ConnectedSystemChange = otherChange,
            AttributeName = "member",
            AttributeType = AttributeDataType.Reference
        };
        var otherSystemValue = new ConnectedSystemObjectChangeAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObjectChangeAttribute = otherChangeAttr,
            ValueChangeType = ValueChangeType.Add,
            StringValue = csoIdsByDn.Keys.First()
        };
        otherChangeAttr.ValueChanges.Add(otherSystemValue);
        otherChange.AttributeChanges.Add(otherChangeAttr);
        seed.Add(otherChange);

        await seed.SaveChangesAsync();

        return new FixupSeed(targetSystem.Id, csoIdsByDn, resolvableValueIds, unresolvableValueIds,
            nonReferenceValue.Id, otherSystemValue.Id);
    }

    /// <summary>
    /// A batch size smaller than the backlog forces multiple bounded UPDATE statements; every
    /// resolvable row must still resolve to the correct CSO, and rows the fixup must not touch
    /// (unresolvable DNs, non-reference values, other systems' values) must remain null.
    /// </summary>
    [Test]
    public async Task FixupCrossBatchChangeRecordReferenceIdsAsync_BatchSizeSmallerThanBacklog_ResolvesAllAcrossBatchesAsync()
    {
        var fixupSeed = await SeedChangeRecordsWithUnresolvedReferencesAsync(resolvableCount: 25);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var resolved = await repository.Sync.FixupCrossBatchChangeRecordReferenceIdsAsync(
            fixupSeed.TargetSystemId, batchSize: 10);

        Assert.That(resolved, Is.EqualTo(25), "Every resolvable value must be resolved across the batches.");

        // ReferenceValueId is a shadow FK (the entity only exposes the ReferenceValue navigation),
        // so read it via EF.Property.
        await using var verify = NewContext();
        var values = await verify.Set<ConnectedSystemObjectChangeAttributeValue>()
            .AsNoTracking()
            .Select(v => new { v.Id, v.StringValue, ReferenceValueId = EF.Property<Guid?>(v, "ReferenceValueId") })
            .ToDictionaryAsync(v => v.Id);

        foreach (var valueId in fixupSeed.ResolvableValueIds)
        {
            var value = values[valueId];
            Assert.That(value.ReferenceValueId, Is.Not.Null, $"Value {valueId} must be resolved.");
            var expectedCsoId = fixupSeed.CsoIdsByDn.Single(kvp =>
                kvp.Key.Equals(value.StringValue, StringComparison.OrdinalIgnoreCase)).Value;
            Assert.That(value.ReferenceValueId, Is.EqualTo(expectedCsoId),
                $"Value {valueId} must resolve to the CSO whose DN matches case-insensitively.");
        }

        foreach (var valueId in fixupSeed.UnresolvableValueIds)
            Assert.That(values[valueId].ReferenceValueId, Is.Null, "Unresolvable DNs must remain unresolved.");

        Assert.That(values[fixupSeed.NonReferenceValueId].ReferenceValueId, Is.Null,
            "Text-typed change values must not be resolved even when their string equals a DN.");
        Assert.That(values[fixupSeed.OtherSystemValueId].ReferenceValueId, Is.Null,
            "Values belonging to another Connected System must not be resolved.");
    }

    /// <summary>
    /// A second invocation finds nothing to resolve and returns zero; unresolvable rows must not
    /// cause repeat work or non-termination.
    /// </summary>
    [Test]
    public async Task FixupCrossBatchChangeRecordReferenceIdsAsync_RunTwice_SecondRunReturnsZeroAsync()
    {
        var fixupSeed = await SeedChangeRecordsWithUnresolvedReferencesAsync(resolvableCount: 5);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var firstRun = await repository.Sync.FixupCrossBatchChangeRecordReferenceIdsAsync(fixupSeed.TargetSystemId, batchSize: 2);
        var secondRun = await repository.Sync.FixupCrossBatchChangeRecordReferenceIdsAsync(fixupSeed.TargetSystemId, batchSize: 2);

        Assert.That(firstRun, Is.EqualTo(5));
        Assert.That(secondRun, Is.Zero, "A fixup with no unresolved references must resolve nothing.");
    }

    /// <summary>
    /// The default batch size path (no explicit batch size) must behave identically for a backlog
    /// that fits in a single batch.
    /// </summary>
    [Test]
    public async Task FixupCrossBatchChangeRecordReferenceIdsAsync_DefaultBatchSize_ResolvesAllAsync()
    {
        var fixupSeed = await SeedChangeRecordsWithUnresolvedReferencesAsync(resolvableCount: 8);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var resolved = await repository.Sync.FixupCrossBatchChangeRecordReferenceIdsAsync(fixupSeed.TargetSystemId);

        Assert.That(resolved, Is.EqualTo(8));

        await using var verify = NewContext();
        var unresolvedResolvable = await verify.Set<ConnectedSystemObjectChangeAttributeValue>()
            .AsNoTracking()
            .CountAsync(v => fixupSeed.ResolvableValueIds.Contains(v.Id) && EF.Property<Guid?>(v, "ReferenceValueId") == null);
        Assert.That(unresolvedResolvable, Is.Zero, "All resolvable values must be resolved by the default batch size path.");
    }
}
