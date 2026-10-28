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
/// Real-PostgreSQL checks that the Connected System Object and Metaverse Object detail reads name every object a
/// value references while reading only what naming needs: a Metaverse Object from its cached display name, a
/// Connected System Object from its naming and identifier values. Reading more made both detail pages take 5 to 18
/// seconds on a group of 594,000 members, where the planner drove the referenced objects' values from the whole
/// attribute value table.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ObjectDetailReferenceLoadDatabaseTests
{
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL object detail reference load tests.");

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

    private sealed record SeededConnectedSystem(int ConnectedSystemId, Guid GroupId);

    /// <summary>
    /// A group whose single-valued managedBy references Ada and whose multi-valued member references Bob. Each user
    /// carries a name, an external id, a secondary external id, and a title that naming does not use.
    /// </summary>
    private async Task<SeededConnectedSystem> SeedConnectedSystemGroupAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband LDAP", ConnectorDefinition = connectorDefinition };
        var userType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var groupType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
        ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType type, string name, AttributeDataType dataType,
            AttributePlurality plurality = AttributePlurality.SingleValued, bool isExternalId = false, bool isSecondaryExternalId = false)
        {
            var attribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = plurality,
                IsExternalId = isExternalId, IsSecondaryExternalId = isSecondaryExternalId, Selected = true
            };
            type.Attributes.Add(attribute);
            return attribute;
        }

        var userId = Attribute(userType, "entryUUID", AttributeDataType.Text, isExternalId: true);
        var userDn = Attribute(userType, "distinguishedName", AttributeDataType.Text, isSecondaryExternalId: true);
        var userDisplayName = Attribute(userType, "displayName", AttributeDataType.Text);
        var userTitle = Attribute(userType, "title", AttributeDataType.Text);
        var groupId = Attribute(groupType, "entryUUID", AttributeDataType.Text, isExternalId: true);
        var groupCn = Attribute(groupType, "cn", AttributeDataType.Text);
        var groupManagedBy = Attribute(groupType, "managedBy", AttributeDataType.Reference);
        var groupMember = Attribute(groupType, "member", AttributeDataType.Reference, AttributePlurality.MultiValued);
        seed.AddRange(connectorDefinition, system, userType, groupType);
        await seed.SaveChangesAsync();

        ConnectedSystemObject User(string uuid, string dn, string displayName, string title)
        {
            var cso = new ConnectedSystemObject
            {
                Type = userType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
                ExternalIdAttributeId = userId.Id, SecondaryExternalIdAttributeId = userDn.Id
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, Attribute = userId, StringValue = uuid });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, Attribute = userDn, StringValue = dn });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, Attribute = userDisplayName, StringValue = displayName });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, Attribute = userTitle, StringValue = title });
            return cso;
        }

        var ada = User("ada-uuid", "cn=ada,ou=People,dc=glitterband,dc=local", "Ada Lovelace", "Analyst");
        var bob = User("bob-uuid", "cn=bob,ou=People,dc=glitterband,dc=local", "Bob Babbage", "Engineer");
        var group = new ConnectedSystemObject
        {
            Type = groupType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = groupId.Id
        };
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = group, Attribute = groupId, StringValue = "engineers-uuid" });
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = group, Attribute = groupCn, StringValue = "Engineers" });
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = group, Attribute = groupManagedBy, ReferenceValue = ada });
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = group, Attribute = groupMember, ReferenceValue = bob });
        seed.AddRange(ada, bob, group);
        await seed.SaveChangesAsync();

        return new SeededConnectedSystem(system.Id, group.Id);
    }

    /// <summary>
    /// A group whose single-valued Manager references Ada and whose multi-valued Static Members references Bob. Both
    /// users carry a Display Name value and the cached display name it produces.
    /// </summary>
    private async Task<Guid> SeedMetaverseGroupAsync()
    {
        await using var seed = NewContext();

        var displayName = new MetaverseAttribute { Name = Constants.BuiltInAttributes.DisplayName, Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var manager = new MetaverseAttribute { Name = "Manager", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued };
        var staticMembers = new MetaverseAttribute { Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        var userType = new MetaverseObjectType { Name = "User", PluralName = "Users" };
        var groupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
        seed.AddRange(displayName, manager, staticMembers, userType, groupType);
        await seed.SaveChangesAsync();

        MetaverseObject User(string name)
        {
            var mvo = new MetaverseObject { Type = userType, CachedDisplayName = name, Created = DateTime.UtcNow };
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = displayName, StringValue = name });
            return mvo;
        }

        var ada = User("Ada Lovelace");
        var bob = User("Bob Babbage");
        var group = new MetaverseObject { Type = groupType, CachedDisplayName = "Engineers", Created = DateTime.UtcNow };
        group.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = group, Attribute = displayName, StringValue = "Engineers" });
        group.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = group, Attribute = manager, ReferenceValue = ada });
        group.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = group, Attribute = staticMembers, ReferenceValue = bob });
        seed.AddRange(ada, bob, group);
        await seed.SaveChangesAsync();

        return group.Id;
    }

    [Test]
    public async Task GetConnectedSystemObjectDetailAsync_SingleAndMultiValuedReferences_NamesEachReferencedObjectAsync()
    {
        // Arrange
        var seeded = await SeedConnectedSystemGroupAsync();

        // Act
        await using var ctx = NewContext();
        var detail = await new PostgresDataRepository(ctx).ConnectedSystems.GetConnectedSystemObjectDetailAsync(
            seeded.ConnectedSystemId, seeded.GroupId, CsoAttributeLoadStrategy.CappedMva);

        // Assert
        Assert.That(detail, Is.Not.Null);
        var values = detail!.ConnectedSystemObject.AttributeValues;
        var managedBy = values.Single(v => v.Attribute.Name == "managedBy").ReferenceValue;
        var member = values.Single(v => v.Attribute.Name == "member").ReferenceValue;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(values, Has.Count.EqualTo(4), "Every value of the group is read");
            Assert.That(managedBy?.NameOrId, Is.EqualTo("Ada Lovelace"), "A single-valued reference is named");
            Assert.That(managedBy?.Type?.Name, Is.EqualTo("user"));
            Assert.That(managedBy?.SecondaryExternalIdAttributeValue?.StringValue, Is.EqualTo("cn=ada,ou=People,dc=glitterband,dc=local"),
                "The secondary external id shown beside a reference is read");
            Assert.That(member?.NameOrId, Is.EqualTo("Bob Babbage"), "A multi-valued reference is named");
            Assert.That(member?.Type?.Name, Is.EqualTo("user"));
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectDetailAsync_ReferencedObject_ReadsOnlyItsNamingAndIdentifierValuesAsync()
    {
        // Arrange
        var seeded = await SeedConnectedSystemGroupAsync();

        // Act
        await using var ctx = NewContext();
        var detail = await new PostgresDataRepository(ctx).ConnectedSystems.GetConnectedSystemObjectDetailAsync(
            seeded.ConnectedSystemId, seeded.GroupId, CsoAttributeLoadStrategy.CappedMva);

        // Assert
        Assert.That(detail, Is.Not.Null);
        var referenced = detail!.ConnectedSystemObject.AttributeValues
            .Where(v => v.ReferenceValue != null)
            .Select(v => v.ReferenceValue!)
            .ToList();
        Assert.That(referenced, Has.Count.EqualTo(2));
        foreach (var cso in referenced)
            Assert.That(cso.AttributeValues.Select(v => v.Attribute.Name), Is.EquivalentTo(new[] { "entryUUID", "distinguishedName", "displayName" }),
                "A referenced object's values beyond its name and identifiers are not read");
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_ConnectedSystemObjectReferences_NamesThemFromNamingAndIdentifierValuesOnlyAsync()
    {
        // Arrange
        var seeded = await SeedConnectedSystemGroupAsync();

        // Act
        await using var ctx = NewContext();
        var range = await new PostgresDataRepository(ctx).ConnectedSystems.GetAttributeValuesRangeAsync(seeded.GroupId, "member", 0, 10);

        // Assert
        var member = range.Results.Single().ReferenceValue;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(member?.NameOrId, Is.EqualTo("Bob Babbage"));
            Assert.That(member?.Type?.Name, Is.EqualTo("user"));
            Assert.That(member?.AttributeValues.Select(v => v.Attribute.Name), Is.EquivalentTo(new[] { "entryUUID", "distinguishedName", "displayName" }),
                "A referenced object's values beyond its name and identifiers are not read");
        }
    }

    [Test]
    public async Task GetMetaverseObjectDetailAsync_SingleAndMultiValuedReferences_NamesThemFromCachedDisplayNameAsync()
    {
        // Arrange
        var groupId = await SeedMetaverseGroupAsync();

        // Act
        await using var ctx = NewContext();
        var detail = await new PostgresDataRepository(ctx).Metaverse.GetMetaverseObjectDetailAsync(groupId, MvoAttributeLoadStrategy.CappedMva);

        // Assert
        Assert.That(detail, Is.Not.Null);
        var values = detail!.MetaverseObject.AttributeValues;
        var manager = values.Single(v => v.Attribute.Name == "Manager").ReferenceValue;
        var member = values.Single(v => v.Attribute.Name == "Static Members").ReferenceValue;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(values, Has.Count.EqualTo(3), "Every value of the group is read");
            Assert.That(manager?.NameOrId, Is.EqualTo("Ada Lovelace"), "A single-valued reference is named");
            Assert.That(manager?.Type?.Name, Is.EqualTo("User"));
            Assert.That(member?.NameOrId, Is.EqualTo("Bob Babbage"), "A multi-valued reference is named");
            Assert.That(member?.Type?.Name, Is.EqualTo("User"));
            Assert.That(manager?.AttributeValues, Is.Empty, "A referenced object is named from its cached display name, not its values");
            Assert.That(member?.AttributeValues, Is.Empty, "A referenced object is named from its cached display name, not its values");
        }
    }

    [Test]
    public async Task GetAttributeValuesRangeAsync_MetaverseObjectReferences_NamesThemFromCachedDisplayNameAsync()
    {
        // Arrange
        var groupId = await SeedMetaverseGroupAsync();

        // Act
        await using var ctx = NewContext();
        var range = await new PostgresDataRepository(ctx).Metaverse.GetAttributeValuesRangeAsync(groupId, "Static Members", 0, 10);

        // Assert
        var member = range.Results.Single().ReferenceValue;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(member?.NameOrId, Is.EqualTo("Bob Babbage"));
            Assert.That(member?.Type?.Name, Is.EqualTo("User"));
            Assert.That(member?.AttributeValues, Is.Empty, "A referenced object is named from its cached display name, not its values");
        }
    }
}
