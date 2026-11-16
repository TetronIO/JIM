// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL round trips of what a Full Import that saves each unit as it goes reads and writes to resolve references
/// (#2521): the id-only reference lookups against the whole-object ones they stand in for, for every anchor type, case rule
/// and the Secondary External ID; the outstanding values read; the sweep's raw write of a value's id, run on a context
/// holding a tracked instance of the value; and the execution item report. Opt-in via the <c>JIM_TEST_RESET_*</c>
/// environment variables; <c>SetUp</c> TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ReferenceSweepRepositoryDatabaseTests
{
    private string _connectionString = null!;
    private int _systemId;
    private readonly Dictionary<string, int> _attributeIds = [];
    private readonly Dictionary<string, int> _typeIds = [];

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL reference sweep repository tests.");
        _connectionString = connectionString;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        _attributeIds.Clear();
        _typeIds.Clear();
        await SeedSchemaAsync();
    }

    [TestCase("text", "ALPHA", "alpha", "beta")]
    [TestCase("exact", "Alpha", "Alpha", "alpha")]
    [TestCase("guid", "6d1d4a2c-0000-4000-8000-00000000a001", "6D1D4A2C-0000-4000-8000-00000000A001", "6d1d4a2c-0000-4000-8000-00000000a002")]
    [TestCase("int", "42", "42", "43")]
    [TestCase("long", "9000000000", "9000000000", "9000000001")]
    [TestCase("decimal", "4200.00", "4200", "4201")]
    public async Task GetConnectedSystemObjectIdsByAttributeValuesAsync_ForEveryAnchorType_FindsWhatTheWholeObjectLookupFindsAsync(
        string anchor, string held, string askedFor, string missing)
    {
        var heldId = await SeedObjectAsync(anchor, held);

        await using var ctx = NewContext();
        var sync = new PostgresDataRepository(ctx).Sync;
        var asked = new[] { askedFor, missing, "not a value of the type" };
        var ids = await sync.GetConnectedSystemObjectIdsByAttributeValuesAsync(_systemId, _attributeIds[anchor], asked);
        var objects = await sync.GetConnectedSystemObjectsByAttributeValuesAsync(_systemId, _attributeIds[anchor], asked);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ids, Is.EquivalentTo(objects.ToDictionary(pair => pair.Key, pair => pair.Value.Id)));
            Assert.That(ids.ContainsKey(askedFor) ? ids[askedFor] : (Guid?)null, Is.EqualTo(heldId));
            Assert.That(ids.ContainsKey(missing), Is.False);
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectIdsBySecondaryExternalIdAnyTypeValuesAsync_FindsWhatTheWholeObjectLookupFindsAsync()
    {
        var heldId = await SeedObjectAsync("text", "dn-holder", secondaryExternalId: "CN=Alpha,DC=example");

        await using var ctx = NewContext();
        var sync = new PostgresDataRepository(ctx).Sync;
        var asked = new[] { "cn=alpha,dc=example", "CN=Beta,DC=example" };
        var ids = await sync.GetConnectedSystemObjectIdsBySecondaryExternalIdAnyTypeValuesAsync(_systemId, asked);
        var objects = await sync.GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(_systemId, asked);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ids, Is.EquivalentTo(objects.ToDictionary(pair => pair.Key, pair => pair.Value.Id)));
            Assert.That(ids["cn=alpha,dc=example"], Is.EqualTo(heldId));
        }
    }

    [Test]
    public async Task ApplyOutstandingReferenceResolutionsAsync_OnAContextTrackingTheValue_WritesTheIdAndLeavesTheTrackerInStepAsync()
    {
        var targetId = await SeedObjectAsync("text", "target");
        var (holderId, outstandingValueId, resolvedValueId) = await SeedHolderAsync("target");

        await using var ctx = NewContext(trackAll: true);
        var sync = new PostgresDataRepository(ctx).Sync;

        // The holder and its values tracked on the context, as an import's run context can hold them.
        var tracked = await ctx.ConnectedSystemObjects.Include(o => o.AttributeValues).SingleAsync(o => o.Id == holderId);
        var outstanding = await sync.GetOutstandingReferenceValuesAsync([holderId]);
        var written = await sync.ApplyOutstandingReferenceResolutionsAsync(
            [new OutstandingReferenceResolution(outstandingValueId, targetId, ExecutionItemId: null, _attributeIds["member"], "target")]);
        var trackedValue = tracked.AttributeValues.Single(v => v.Id == outstandingValueId);
        var trackedState = ctx.Entry(trackedValue).Property(v => v.ReferenceValueId).IsModified;
        tracked.LastUpdated = DateTime.UtcNow;
        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.Nothing, "a later save on the context neither fails nor writes the old id back");

        await using var read = NewContext();
        var stored = await read.ConnectedSystemObjectAttributeValues.AsNoTracking().SingleAsync(v => v.Id == outstandingValueId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outstanding, Is.EqualTo(new[]
            {
                new OutstandingReferenceValue(outstandingValueId, holderId, _typeIds["holder"], _attributeIds["member"], "target")
            }), "only the value with no id yet is outstanding; the resolved one is not read");
            Assert.That(written, Is.EqualTo(1));
            Assert.That(stored.ReferenceValueId, Is.EqualTo(targetId));
            Assert.That(trackedValue.ReferenceValueId, Is.EqualTo(targetId));
            Assert.That(trackedState, Is.False);
            Assert.That(resolvedValueId, Is.Not.EqualTo(outstandingValueId));
        }

        // Applied again, a value that already has its id is not re-pointed.
        await using var again = NewContext();
        var rewritten = await new PostgresDataRepository(again).Sync.ApplyOutstandingReferenceResolutionsAsync(
            [new OutstandingReferenceResolution(outstandingValueId, holderId, ExecutionItemId: null, _attributeIds["member"], "target")]);
        Assert.That(rewritten, Is.Zero);
    }

    [Test]
    public async Task UpdateUnresolvedReferenceReportsAsync_WritesTheCountAndOnlyTheErrorsItCarriesAsync()
    {
        await using (var seed = NewContext())
        {
            var activity = new Activity { TargetName = "Full Import", Status = ActivityStatus.InProgress, ConnectedSystemId = _systemId };
            seed.Activities.Add(activity);
            await seed.SaveChangesAsync();

            var items = new[] { Guid.NewGuid(), Guid.NewGuid() }.Select(id => new ActivityRunProfileExecutionItem
            {
                Id = id, ActivityId = activity.Id, ObjectChangeType = ObjectChangeType.Added
            }).ToList();
            items[1].ErrorType = ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed;
            items[1].ErrorMessage = "kept";
            items[1].ErrorStackTrace = "kept trace";
            await new PostgresDataRepository(seed).Sync.BulkInsertRpeisAsync(items);

            await using var ctx = NewContext();
            await new PostgresDataRepository(ctx).Sync.UpdateUnresolvedReferenceReportsAsync(
            [
                new UnresolvedReferenceReport(items[0].Id, 2, ActivityRunProfileExecutionItemErrorType.UnresolvedReference, "two unresolved"),
                new UnresolvedReferenceReport(items[1].Id, 1, ErrorType: null, ErrorMessage: null)
            ]);

            await using var read = NewContext();
            var stored = await read.ActivityRunProfileExecutionItems.AsNoTracking().Where(i => i.ActivityId == activity.Id).ToDictionaryAsync(i => i.Id);
            using (Assert.EnterMultipleScope())
            {
                Assert.That((stored[items[0].Id].UnresolvedReferenceCount, stored[items[0].Id].ErrorType, stored[items[0].Id].ErrorMessage),
                    Is.EqualTo(((int?)2, (ActivityRunProfileExecutionItemErrorType?)ActivityRunProfileExecutionItemErrorType.UnresolvedReference, "two unresolved")));
                Assert.That((stored[items[1].Id].UnresolvedReferenceCount, stored[items[1].Id].ErrorType, stored[items[1].Id].ErrorMessage, stored[items[1].Id].ErrorStackTrace),
                    Is.EqualTo(((int?)1, (ActivityRunProfileExecutionItemErrorType?)ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed, "kept", "kept trace")));
            }
        }
    }

    #region Harness

    private async Task SeedSchemaAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };

        ConnectedSystemObjectType ObjectType(string name, AttributeDataType anchorType, bool caseExact = false, bool withSecondary = false, bool withMember = false)
        {
            var objectType = new ConnectedSystemObjectType { Name = name, ConnectedSystem = system, Selected = true };
            objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
            {
                Name = $"{name}Anchor", ConnectedSystemObjectType = objectType, Type = anchorType, CaseExact = caseExact,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
            });
            if (withSecondary)
            {
                objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
                {
                    Name = $"{name}Dn", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Text,
                    AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsSecondaryExternalId = true
                });
            }

            if (withMember)
            {
                objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
                {
                    Name = "member", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Reference,
                    AttributePlurality = AttributePlurality.MultiValued, Selected = true
                });
            }

            return objectType;
        }

        var types = new[]
        {
            ObjectType("text", AttributeDataType.Text, withSecondary: true),
            ObjectType("exact", AttributeDataType.Text, caseExact: true),
            ObjectType("guid", AttributeDataType.Guid),
            ObjectType("int", AttributeDataType.Number),
            ObjectType("long", AttributeDataType.LongNumber),
            ObjectType("decimal", AttributeDataType.Decimal),
            ObjectType("holder", AttributeDataType.Text, withMember: true)
        };
        seed.AddRange(connectorDefinition, system);
        seed.AddRange(types);
        await seed.SaveChangesAsync();

        _systemId = system.Id;
        foreach (var type in types)
        {
            _typeIds[type.Name] = type.Id;
            _attributeIds[type.Name] = type.Attributes.Single(a => a.IsExternalId).Id;
            foreach (var attribute in type.Attributes.Where(a => !a.IsExternalId))
                _attributeIds[attribute.Name == "member" ? "member" : $"{type.Name}.dn"] = attribute.Id;
        }
    }

    private async Task<Guid> SeedObjectAsync(string type, string anchor, string? secondaryExternalId = null)
    {
        var id = Guid.NewGuid();
        var cso = new ConnectedSystemObject
        {
            Id = id, ConnectedSystemId = _systemId, TypeId = _typeIds[type], ExternalIdAttributeId = _attributeIds[type],
            SecondaryExternalIdAttributeId = secondaryExternalId == null ? null : _attributeIds[$"{type}.dn"],
            Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow
        };
        var anchorValue = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _attributeIds[type] };
        switch (type)
        {
            case "guid": anchorValue.GuidValue = Guid.Parse(anchor); break;
            case "int": anchorValue.IntValue = int.Parse(anchor); break;
            case "long": anchorValue.LongValue = long.Parse(anchor); break;
            case "decimal": anchorValue.DecimalValue = decimal.Parse(anchor, System.Globalization.CultureInfo.InvariantCulture); break;
            default: anchorValue.StringValue = anchor; break;
        }

        cso.AttributeValues.Add(anchorValue);
        if (secondaryExternalId != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _attributeIds[$"{type}.dn"], StringValue = secondaryExternalId });

        await using var write = NewContext();
        await new PostgresDataRepository(write).Sync.CreateConnectedSystemObjectsAsync([cso]);
        return id;
    }

    /// <summary>An object with two reference values: one written by identifier alone, one already holding its object's id.</summary>
    private async Task<(Guid HolderId, Guid OutstandingValueId, Guid ResolvedValueId)> SeedHolderAsync(string reference)
    {
        var holderId = Guid.NewGuid();
        var outstandingValueId = Guid.NewGuid();
        var resolvedValueId = Guid.NewGuid();
        var cso = new ConnectedSystemObject
        {
            Id = holderId, ConnectedSystemId = _systemId, TypeId = _typeIds["holder"], ExternalIdAttributeId = _attributeIds["holder"],
            Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _attributeIds["holder"], StringValue = "holder" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = outstandingValueId, AttributeId = _attributeIds["member"], UnresolvedReferenceValue = reference });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = resolvedValueId, AttributeId = _attributeIds["member"], UnresolvedReferenceValue = "self", ReferenceValueId = holderId
        });

        await using var write = NewContext();
        await new PostgresDataRepository(write).Sync.CreateConnectedSystemObjectsAsync([cso]);
        return (holderId, outstandingValueId, resolvedValueId);
    }

    #endregion
}
