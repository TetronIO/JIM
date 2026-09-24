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
/// Real-PostgreSQL verification of <c>SyncRepository.DeleteConnectedSystemObjectsByIdsAsync</c>
///: deleting a Connected System Object by id, via raw SQL, on a context that
/// already holds tracked instances of the CSO, its attribute values, and another CSO's attribute
/// value that references it.
/// </summary>
/// <remarks>
/// Export batches load Pending Exports (and their CSO graph) <c>AsNoTracking()</c>, so the method
/// itself never sees a tracked instance in production. This suite proves the worker-safe case
/// anyway - a tracked graph left behind by an earlier query in the same run - because the raw
/// DELETE bypasses the change tracker exactly as the sibling
/// <c>DeletePendingExportsByConnectedSystemObjectIdsAsync</c> does, and the in-memory provider has
/// no relational row-count or foreign-key checks to catch a fix-up bug here. Opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class DeleteConnectedSystemObjectsByIdsDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Connected System Object delete-by-id tests.");

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

    /// <summary>
    /// Seeds two CSOs of a "Group"-shaped type: one to be deleted, and one whose "member" Reference
    /// attribute value points at it via ReferenceValueId, mirroring a still-referenced CSO in a
    /// Connected System (e.g. a group membership pointing at a deprovisioned user).
    /// </summary>
    private async Task<(Guid DeletedCsoId, Guid ReferencingValueId)> SeedCsoWithReferencingValueAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Target System", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "GROUP", ConnectedSystem = system, Selected = true };
        var cnAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "cn", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        csType.Attributes.Add(cnAttr);
        csType.Attributes.Add(memberAttr);
        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();

        var deletedCso = new ConnectedSystemObject
        {
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            ExternalIdAttributeId = cnAttr.Id
        };
        deletedCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = cnAttr,
            AttributeId = cnAttr.Id,
            StringValue = "cancelled-provisioning-group"
        });

        var referencingCso = new ConnectedSystemObject
        {
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined
        };
        seed.Add(deletedCso);
        seed.Add(referencingCso);
        await seed.SaveChangesAsync();

        var referencingValue = new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = referencingCso,
            Attribute = memberAttr,
            AttributeId = memberAttr.Id,
            ReferenceValueId = deletedCso.Id
        };
        seed.Add(referencingValue);
        await seed.SaveChangesAsync();

        return (deletedCso.Id, referencingValue.Id);
    }

    [Test]
    public async Task DeleteConnectedSystemObjectsByIdsAsync_TrackedCsoAndReferencingValue_DeletesAndClearsReferenceWithoutThrowingAsync()
    {
        var (deletedCsoId, referencingValueId) = await SeedCsoWithReferencingValueAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Load TRACKED instances of the CSO under deletion (and its attribute values), plus the
        // other CSO's tracked attribute value that references it - mirroring a worker context where
        // an earlier query in the same run left these tracked.
        var trackedCso = await ctx.ConnectedSystemObjects
            .Include(c => c.AttributeValues)
            .SingleAsync(c => c.Id == deletedCsoId);
        Assert.That(trackedCso.AttributeValues, Is.Not.Empty, "The seeded CSO must have tracked attribute values.");

        var trackedReferencingValue = await ctx.ConnectedSystemObjectAttributeValues
            .SingleAsync(av => av.Id == referencingValueId);
        Assert.That(trackedReferencingValue.ReferenceValueId, Is.EqualTo(deletedCsoId));

        var deletedCount = await repository.Sync.DeleteConnectedSystemObjectsByIdsAsync([deletedCsoId]);
        Assert.That(deletedCount, Is.EqualTo(1));

        // The raw delete must have fixed up or detached every tracked instance it touched; if not,
        // this SaveChangesAsync tries to write a stale value back against an already-deleted row and
        // throws DbUpdateConcurrencyException (see DetachTrackedEntities' remarks).
        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.Nothing);

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConnectedSystemObjects.AnyAsync(c => c.Id == deletedCsoId), Is.False,
                "The Connected System Object must be deleted.");
            Assert.That(await verify.ConnectedSystemObjectAttributeValues.AnyAsync(av => av.Id == trackedCso.AttributeValues.Single().Id), Is.False,
                "The deleted CSO's own attribute values must cascade away.");

            var reloadedReferencingValue = await verify.ConnectedSystemObjectAttributeValues.SingleAsync(av => av.Id == referencingValueId);
            Assert.That(reloadedReferencingValue.ReferenceValueId, Is.Null,
                "The other CSO's reference to the deleted CSO must be nulled, not left dangling or FK-violating.");
        }
    }
}
