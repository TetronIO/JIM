// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of <see cref="JIM.PostgresData.Repositories.ConnectedSystemRepository.GetPendingExportByConnectedSystemObjectIdForMergeAsync"/>,
/// the lean fetch added for the merge-and-replace path in
/// <c>ExportEvaluationServer.CreateOrUpdatePendingExportWithNoNetChangeAsync</c> (issue #986).
/// </summary>
/// <remarks>
/// The merge path re-fetches a Pending Export once per removed group member during cohort
/// deprovisioning. Instrumented capture showed 99.5% of the merge cost is this fetch, because the
/// pre-existing <see cref="JIM.PostgresData.Repositories.ConnectedSystemRepository.GetPendingExportByConnectedSystemObjectIdAsync"/>
/// Include chain also loads the target Connected System Object's and source Metaverse Object's full
/// attribute value graphs, up to ~200,000 rows each for a large group, even though the merge logic
/// only ever reads AttributeValueChanges. EF Core's in-memory provider (and Moq-backed
/// <c>IQueryable</c> mocks) cannot distinguish an Include-shaped query from a full one, since every
/// seeded object is already a fully wired-up graph in memory; this can only be verified against a
/// real database. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other
/// <c>RequiresPostgres</c> fixtures (see <c>JIM.Worker.Tests.Servers.SystemResetDatabaseTests</c>);
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportMergeFetchDatabaseTests
{
    private const int LargeAttributeValueCount = 500;

    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export merge-fetch tests.");

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

    /// <summary>
    /// Seeds a large group-shaped CSO (many attribute values) and source MVO (many attribute values),
    /// plus a Pending Export for that CSO with a small attribute value change set - mirroring a
    /// group's Pending Export mid-cohort-deprovisioning: a huge membership graph behind it, but only
    /// a handful of pending attribute changes.
    /// </summary>
    private async Task<(Guid CsoId, Guid PendingExportId)> SeedLargeGroupWithSmallPendingExportAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "GROUP", ConnectedSystem = system, Selected = true };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        var titleAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "title", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(memberAttr);
        csType.Attributes.Add(titleAttr);

        var mvType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
        var mvMemberAttr = new MetaverseAttribute
        {
            Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, BuiltIn = true
        };
        mvType.Attributes.Add(mvMemberAttr);

        seed.AddRange(connectorDefinition, system, csType, mvType);
        await seed.SaveChangesAsync();

        var cso = new ConnectedSystemObject { Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal };
        for (var i = 0; i < LargeAttributeValueCount; i++)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = memberAttr, StringValue = $"CN=User{i:D4},DC=test", ConnectedSystemObject = cso });

        var mvo = new MetaverseObject { Type = mvType };
        for (var i = 0; i < LargeAttributeValueCount; i++)
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = mvMemberAttr, MetaverseObject = mvo, GuidValue = Guid.NewGuid() });

        seed.Add(cso);
        seed.Add(mvo);
        await seed.SaveChangesAsync();

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = system,
            ConnectedSystemId = system.Id,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            SourceMetaverseObject = mvo,
            SourceMetaverseObjectId = mvo.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), Attribute = titleAttr, AttributeId = titleAttr.Id,
            StringValue = "Old Title", ChangeType = PendingExportAttributeChangeType.Update
        });
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), Attribute = memberAttr, AttributeId = memberAttr.Id,
            StringValue = "CN=Leaver1,DC=test", ChangeType = PendingExportAttributeChangeType.Remove
        });
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), Attribute = memberAttr, AttributeId = memberAttr.Id,
            StringValue = "CN=Leaver2,DC=test", ChangeType = PendingExportAttributeChangeType.Remove
        });

        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        return (cso.Id, pendingExport.Id);
    }

    /// <summary>
    /// Sharpest red for issue #986: the lean merge fetch must NOT load the target CSO's or source MVO's
    /// attribute value graphs, only AttributeValueChanges (with Attribute). Before the lean Include
    /// shape is implemented, this fails because the method under test still delegates to the heavy
    /// fetch, which loads everything.
    /// </summary>
    [Test]
    public async Task GetPendingExportByConnectedSystemObjectIdForMergeAsync_DoesNotLoadCsoOrMvoAttributeValueGraphsAsync()
    {
        var (csoId, _) = await SeedLargeGroupWithSmallPendingExportAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetPendingExportByConnectedSystemObjectIdForMergeAsync(csoId);

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.ConnectedSystemObject, Is.Null,
                "Merge fetch must not load the target CSO navigation - the merge logic never reads it, and for a large group it can be hundreds of thousands of rows.");
            Assert.That(result!.SourceMetaverseObject, Is.Null,
                "Merge fetch must not load the source MVO navigation - the merge logic never reads it.");
            Assert.That(result!.ConnectedSystem, Is.Null,
                "Merge fetch must not load the ConnectedSystem navigation - the merge logic never reads it.");
            Assert.That(result!.AttributeValueChanges, Has.Count.EqualTo(3),
                "Merge fetch must still load AttributeValueChanges - this is the actual merge input.");
            Assert.That(result!.AttributeValueChanges.All(avc => avc.Attribute != null), Is.True,
                "Each AttributeValueChange's Attribute must be loaded - GetAttributeChangeMergeKey reads AttributePlurality from it.");
        });
    }

    /// <summary>
    /// Equality-of-merge-result half of the red test: whatever the lean fetch returns for
    /// AttributeValueChanges must be identical (Id, AttributeId, value columns, ChangeType, and the
    /// Attribute's AttributePlurality used for merge-key dedup) to what the heavy fetch returns, so
    /// swapping the merge call site to the lean method cannot change the merge outcome.
    /// </summary>
    [Test]
    public async Task GetPendingExportByConnectedSystemObjectIdForMergeAsync_AttributeValueChangesMatchHeavyFetchAsync()
    {
        var (csoId, _) = await SeedLargeGroupWithSmallPendingExportAsync();

        await using var heavyCtx = NewContext();
        var heavyResult = await new PostgresDataRepository(heavyCtx).ConnectedSystems.GetPendingExportByConnectedSystemObjectIdAsync(csoId);

        await using var leanCtx = NewContext();
        var leanResult = await new PostgresDataRepository(leanCtx).ConnectedSystems.GetPendingExportByConnectedSystemObjectIdForMergeAsync(csoId);

        Assert.That(heavyResult, Is.Not.Null);
        Assert.That(leanResult, Is.Not.Null);

        var heavyChanges = heavyResult!.AttributeValueChanges
            .OrderBy(c => c.Id)
            .Select(c => (c.Id, c.AttributeId, c.StringValue, c.ChangeType, Plurality: c.Attribute?.AttributePlurality))
            .ToList();
        var leanChanges = leanResult!.AttributeValueChanges
            .OrderBy(c => c.Id)
            .Select(c => (c.Id, c.AttributeId, c.StringValue, c.ChangeType, Plurality: c.Attribute?.AttributePlurality))
            .ToList();

        Assert.That(leanChanges, Is.EqualTo(heavyChanges),
            "The lean fetch's AttributeValueChanges (the actual merge input) must be identical to the heavy fetch's, so the merge-and-replace outcome is unaffected by which fetch method supplied the data.");
    }
}
