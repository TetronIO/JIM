// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Regression tests for issue #782: a Connected System schema refresh reported success but the
/// discovered object types and attributes were never persisted, so the selection UI never rendered.
///
/// The root cause was that <see cref="ConnectedSystemRepository.UpdateConnectedSystemAsync"/> persists
/// the Connected System root plus its partitions and setting values, but not its ObjectTypes collection.
/// On the web write path the Connected System is loaded by a transient (disposed) DbContext, so the
/// collection arrives detached and the new object types (Id == 0) are never inserted.
///
/// These tests reproduce the multi-context detached scenario by using separate <see cref="JimDbContext"/>
/// instances over one shared in-memory database, then assert that the dedicated schema-persistence method
/// reconciles object types and attributes correctly.
/// </summary>
[TestFixture]
public class ConnectedSystemSchemaPersistenceTests
{
    private string _dbName = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _dbName = Guid.NewGuid().ToString();
    }

    private DbContextOptions<JimDbContext> Options() => new DbContextOptionsBuilder<JimDbContext>()
        .UseInMemoryDatabase(databaseName: _dbName)
        .EnableSensitiveDataLogging()
        .Options;

    /// <summary>
    /// Seeds an empty Connected System and returns its id. The context is disposed before returning,
    /// mimicking the web layer's transient-per-operation DbContext.
    /// </summary>
    private async Task<int> SeedConnectedSystemAsync()
    {
        await using var ctx = new JimDbContext(Options());
        var cs = new ConnectedSystem
        {
            Name = "dummy 1",
            ConnectorDefinition = new ConnectorDefinition { Name = "Test Connector" }
        };
        ctx.ConnectedSystems.Add(cs);
        await ctx.SaveChangesAsync();
        return cs.Id;
    }

    /// <summary>
    /// Loads the Connected System graph (no tracking) through its own context, which is then disposed,
    /// so the returned instance is fully detached, as it is on the web write path.
    /// </summary>
    private async Task<ConnectedSystem> LoadDetachedAsync(int csId)
    {
        await using var ctx = new JimDbContext(Options());
        // Load the root and its object types separately, exactly as production GetConnectedSystemAsync does
        // (a single deep nullable Include chain does not translate cleanly), so the returned graph is detached.
        var cs = await ctx.ConnectedSystems.AsNoTracking().SingleAsync(x => x.Id == csId);
        cs.ObjectTypes = await ctx.ConnectedSystemObjectTypes
            .AsNoTracking()
            .Include(ot => ot.Attributes)
            .Where(ot => ot.ConnectedSystemId == csId)
            .ToListAsync();
        return cs;
    }

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_NewObjectTypesAndAttributes_ArePersisted()
    {
        // Arrange: an empty system, then a detached graph with a freshly-discovered object type.
        var csId = await SeedConnectedSystemAsync();
        var detachedCs = await LoadDetachedAsync(csId);

        detachedCs.ObjectTypes = new List<ConnectedSystemObjectType>
        {
            new ConnectedSystemObjectType
            {
                Name = "User",
                Selected = true,
                Attributes = new List<ConnectedSystemObjectTypeAttribute>
                {
                    new ConnectedSystemObjectTypeAttribute { Name = "employeeId", IsExternalId = true, Selected = true },
                    new ConnectedSystemObjectTypeAttribute { Name = "displayName" }
                }
            }
        };

        // Act: persist through a fresh context, as the import does.
        await using (var ctx = new JimDbContext(Options()))
            await new PostgresDataRepository(ctx).ConnectedSystems.UpdateConnectedSystemSchemaAsync(detachedCs);

        // Assert: reload through yet another context and confirm the schema survived.
        await using (var ctx = new JimDbContext(Options()))
        {
            var types = await ctx.ConnectedSystemObjectTypes
                .Include(ot => ot.Attributes)
                .Where(ot => ot.ConnectedSystemId == csId)
                .ToListAsync();

            Assert.That(types.Count, Is.EqualTo(1), "object type was not persisted");
            var user = types[0];
            Assert.That(user.Name, Is.EqualTo("User"));
            Assert.That(user.Selected, Is.True, "object type Selected flag was not persisted");
            Assert.That(user.Attributes.Count, Is.EqualTo(2), "attributes were not persisted");
            Assert.That(user.Attributes.Single(a => a.Name == "employeeId").IsExternalId, Is.True);
        }
    }

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_RefreshAddsAttributeToExistingType_RetainsExistingAndAddsNew()
    {
        // Arrange: seed a system that already has one object type with one attribute.
        var csId = await SeedConnectedSystemAsync();
        await using (var ctx = new JimDbContext(Options()))
        {
            ctx.ConnectedSystemObjectTypes.Add(new ConnectedSystemObjectType
            {
                Name = "User",
                ConnectedSystemId = csId,
                Attributes = new List<ConnectedSystemObjectTypeAttribute>
                {
                    new ConnectedSystemObjectTypeAttribute { Name = "employeeId", IsExternalId = true, Selected = true }
                }
            });
            await ctx.SaveChangesAsync();
        }

        var detachedCs = await LoadDetachedAsync(csId);
        var existingType = detachedCs.ObjectTypes!.Single();
        var existingAttributeId = existingType.Attributes.Single().Id;

        // A refresh adds a second attribute, preserving the existing one (and its Id, as the import does).
        existingType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Name = "displayName" });

        // Act
        await using (var ctx = new JimDbContext(Options()))
            await new PostgresDataRepository(ctx).ConnectedSystems.UpdateConnectedSystemSchemaAsync(detachedCs);

        // Assert
        await using (var ctx = new JimDbContext(Options()))
        {
            var types = await ctx.ConnectedSystemObjectTypes
                .Include(ot => ot.Attributes)
                .Where(ot => ot.ConnectedSystemId == csId)
                .ToListAsync();

            Assert.That(types.Count, Is.EqualTo(1), "object type count changed unexpectedly");
            var attrs = types[0].Attributes.OrderBy(a => a.Name).ToList();
            Assert.That(attrs.Select(a => a.Name), Is.EquivalentTo(new[] { "displayName", "employeeId" }));
            // the pre-existing attribute keeps its Id (no delete + re-insert)
            Assert.That(attrs.Single(a => a.Name == "employeeId").Id, Is.EqualTo(existingAttributeId));
        }
    }

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_SelectionChangeOnExistingAttribute_IsPersisted()
    {
        // Arrange: an existing, unselected attribute (the state after schema retrieval, before the admin selects).
        var csId = await SeedConnectedSystemAsync();
        await using (var ctx = new JimDbContext(Options()))
        {
            ctx.ConnectedSystemObjectTypes.Add(new ConnectedSystemObjectType
            {
                Name = "User",
                ConnectedSystemId = csId,
                Selected = false,
                Attributes = new List<ConnectedSystemObjectTypeAttribute>
                {
                    new ConnectedSystemObjectTypeAttribute { Name = "displayName", Selected = false }
                }
            });
            await ctx.SaveChangesAsync();
        }

        var detachedCs = await LoadDetachedAsync(csId);
        // Mimic the Blazor "Save Changes" path: the admin selects the type and an attribute.
        var selectedType = detachedCs.ObjectTypes!.Single();
        selectedType.Selected = true;
        selectedType.Attributes.Single().Selected = true;

        // Act
        await using (var ctx = new JimDbContext(Options()))
            await new PostgresDataRepository(ctx).ConnectedSystems.UpdateConnectedSystemSchemaAsync(detachedCs);

        // Assert
        await using (var ctx = new JimDbContext(Options()))
        {
            var type = await ctx.ConnectedSystemObjectTypes
                .Include(ot => ot.Attributes)
                .SingleAsync(ot => ot.ConnectedSystemId == csId);

            Assert.That(type.Selected, Is.True, "object type selection was not persisted");
            Assert.That(type.Attributes.Single().Selected, Is.True, "attribute selection was not persisted");
        }
    }
}
