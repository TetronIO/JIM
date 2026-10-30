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
/// Real-PostgreSQL coverage that a synchronisation page's Metaverse Objects stay the one instance the page works on
/// (#2598, Phase 2). The page loads them untracked, so a tracked load later in the page that reaches the same row
/// makes a second instance, and EF's relationship fix-up can point the page's Connected System Objects at it: a join
/// match loaded a matched object's referenced objects with only their display name, and contributor re-election loaded
/// a survivor's Metaverse Object with every value. A page object swapped for a partial copy would then have its other
/// values deleted by the bulk update. The loads the page makes after its own must therefore leave Metaverse Objects
/// untracked. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PageMetaverseObjectIdentityDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext(QueryTrackingBehavior tracking = QueryTrackingBehavior.NoTracking)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(tracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL page Metaverse Object identity tests.");

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

    [Test]
    public async Task GetMetaverseObjectForJoinMatchAsync_MatchReferencesAPageObject_LeavesThePageObjectWholeAndNothingTrackedAsync()
    {
        var s = await SeedAsync();
        await using var pageContext = PageContext();
        var repository = new PostgresDataRepository(pageContext);
        var manager = await LoadPageObjectAsync(repository, s);

        // A new account matches the report, whose manager is the page's object.
        var match = await repository.Sync.GetMetaverseObjectForJoinMatchAsync(s.ReportId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(match, Is.Not.Null);
            Assert.That(match!.AttributeValues, Has.Count.EqualTo(2), "The matched object carries its own values.");
            await AssertPageObjectIntactAsync(s, manager, pageContext);
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectWithAttributeValuesAsync_SurvivorJoinedToAPageObject_LeavesThePageObjectWholeAndNothingTrackedAsync()
    {
        var s = await SeedAsync();
        await using var pageContext = PageContext();
        var repository = new PostgresDataRepository(pageContext);
        var manager = await LoadPageObjectAsync(repository, s);

        // Re-election hydrates the other system's object joined to the same Metaverse Object.
        var survivor = await repository.Sync.GetConnectedSystemObjectWithAttributeValuesAsync(s.DirectorySystemId, s.DirectoryCsoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(survivor, Is.Not.Null);
            Assert.That(survivor!.AttributeValues, Has.Count.EqualTo(1), "The survivor carries its own values.");
            Assert.That(survivor.Type, Is.Not.Null);
            Assert.That(survivor.MetaverseObjectId, Is.EqualTo(s.ManagerId));
            await AssertPageObjectIntactAsync(s, manager, pageContext);
        }
    }

    /// <summary>
    /// As a synchronisation page runs: a tracking context with change detection off from the load to the tracker clear.
    /// </summary>
    private JimDbContext PageContext()
    {
        var context = NewContext(QueryTrackingBehavior.TrackAll);
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        return context;
    }

    private static async Task<MetaverseObject> LoadPageObjectAsync(PostgresDataRepository repository, Seeded s)
    {
        var page = await repository.Sync.GetConnectedSystemObjectsAsync(s.HrSystemId, 1, 100, afterId: Guid.Empty);
        var manager = page.Results.Single(c => c.Id == s.HrCsoId).MetaverseObject!;
        Assert.That(manager.AttributeValues, Has.Count.EqualTo(2), "The page object is loaded with every value.");
        return manager;
    }

    private static async Task AssertPageObjectIntactAsync(Seeded s, MetaverseObject manager, JimDbContext pageContext)
    {
        var pageCso = await pageContext.ConnectedSystemObjects.FindAsync(s.HrCsoId);
        Assert.That(pageCso!.MetaverseObject, Is.SameAs(manager), "The page's Connected System Object still points at the page's instance.");
        Assert.That(manager.AttributeValues, Has.Count.EqualTo(2), "The page's instance still holds every value.");
        Assert.That(pageContext.ChangeTracker.Entries<MetaverseObject>(), Is.Empty, "No Metaverse Object is tracked.");
        Assert.That(pageContext.ChangeTracker.Entries<MetaverseObjectAttributeValue>(), Is.Empty, "No Metaverse Object attribute value is tracked.");
    }

    private sealed record Seeded(int HrSystemId, int DirectorySystemId, Guid HrCsoId, Guid DirectoryCsoId, Guid ManagerId, Guid ReportId);

    /// <summary>
    /// A manager joined to an HR record and a directory account, and a report whose manager attribute references them.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var hr = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition };
        var directory = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
        var hrType = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = hr, Selected = true };
        var directoryType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = directory, Selected = true };
        var hrId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeId", ConnectedSystemObjectType = hrType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var directoryId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = directoryType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        hrType.Attributes.Add(hrId);
        directoryType.Attributes.Add(directoryId);

        var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var displayName = new MetaverseAttribute { Name = Constants.BuiltInAttributes.DisplayName, Type = AttributeDataType.Text };
        var employeeId = new MetaverseAttribute { Name = "Employee ID", Type = AttributeDataType.Text };
        var managerAttribute = new MetaverseAttribute { Name = "Manager", Type = AttributeDataType.Reference };

        var manager = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
        manager.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = manager, Attribute = displayName, StringValue = "Pat Manager" });
        manager.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = manager, Attribute = employeeId, StringValue = "E1" });
        var report = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
        report.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = report, Attribute = displayName, StringValue = "Sam Report" });
        report.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = report, Attribute = managerAttribute, ReferenceValue = manager });

        seed.AddRange(connectorDefinition, hr, directory, hrType, directoryType, mvoType, displayName, employeeId, managerAttribute, manager, report);
        await seed.SaveChangesAsync();

        ConnectedSystemObject Cso(ConnectedSystem system, ConnectedSystemObjectType type, ConnectedSystemObjectTypeAttribute idAttribute, string value)
        {
            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(), ConnectedSystem = system, Type = type, ExternalIdAttributeId = idAttribute.Id,
                Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow,
                MetaverseObject = manager, JoinType = ConnectedSystemObjectJoinType.Joined
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = idAttribute, AttributeId = idAttribute.Id, StringValue = value });
            seed.Add(cso);
            return cso;
        }

        var hrCso = Cso(hr, hrType, hrId, "E1");
        var directoryCso = Cso(directory, directoryType, directoryId, "G1");
        await seed.SaveChangesAsync();

        return new Seeded(hr.Id, directory.Id, hrCso.Id, directoryCso.Id, manager.Id, report.Id);
    }
}
