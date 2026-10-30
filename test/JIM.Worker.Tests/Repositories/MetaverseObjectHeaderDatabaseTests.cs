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
/// Real-PostgreSQL verification that a Metaverse Object's header is a header (#2498): the item, Activity and
/// Operations pages and the REST API's existence checks read it, and it used to materialise every attribute value
/// and each reference's Display Name, which for a large group meant every member.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseObjectHeaderDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Metaverse Object header tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    [Test]
    public async Task GetMetaverseObjectHeaderAsync_GroupWithMembers_ReadsNoAttributeValuesAndNamesItFromItsCachedDisplayNameAsync()
    {
        var groupId = await SeedGroupAsync(memberCount: 50);

        await using var ctx = NewContext();
        var header = await new PostgresDataRepository(ctx).Metaverse.GetMetaverseObjectHeaderAsync(groupId);

        Assert.That(header, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header!.AttributeValues, Is.Empty, "a header never reads the object's values; its members least of all");
            Assert.That(header!.NameOrId, Is.EqualTo("All Staff"), "named from the cached display name, by the same policy as its values");
            Assert.That(header!.TypeName, Is.EqualTo("Group"));
            Assert.That(header!.TypePluralName, Is.EqualTo("Groups"));
            Assert.That(header!.ScopeReviewPending, Is.True);
        }
    }

    [Test]
    public async Task GetMetaverseObjectHeaderAsync_NoSuchObject_ReturnsNullAsync()
    {
        await using var ctx = NewContext();
        var header = await new PostgresDataRepository(ctx).Metaverse.GetMetaverseObjectHeaderAsync(Guid.NewGuid());

        Assert.That(header, Is.Null);
    }

    /// <summary>
    /// A Connected System Object's detail names the Metaverse Object it is joined to (#2498) from its cached display
    /// name: it used to load the joined object's every attribute value to do so, which for a group is every member.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectDetailAsync_JoinedToALargeGroup_NamesItWithoutReadingItsMembersAsync()
    {
        var groupId = await SeedGroupAsync(memberCount: 50);
        int connectedSystemId;
        var csoId = Guid.NewGuid();
        await using (var ctx = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
            var type = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
            ctx.AddRange(connectorDefinition, system, type);
            await ctx.SaveChangesAsync();
            connectedSystemId = system.Id;
            ctx.Add(new ConnectedSystemObject
            {
                Id = csoId, ConnectedSystemId = system.Id, TypeId = type.Id, Created = DateTime.UtcNow,
                MetaverseObjectId = groupId, JoinType = ConnectedSystemObjectJoinType.Joined
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = NewContext();
        var detail = await new PostgresDataRepository(read).ConnectedSystems.GetConnectedSystemObjectDetailAsync(
            connectedSystemId, csoId, CsoAttributeLoadStrategy.CappedMva);

        var joined = detail!.ConnectedSystemObject.MetaverseObject;
        Assert.That(joined, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(joined!.AttributeValues, Is.Empty, "the joined object is named, not loaded");
            Assert.That(joined!.NameOrId, Is.EqualTo("All Staff"));
            Assert.That(joined!.Type.PluralName, Is.EqualTo("Groups"), "its type still builds the link to it");
        }
    }

    private async Task<Guid> SeedGroupAsync(int memberCount)
    {
        await using var ctx = NewContext();
        var groups = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
        var people = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var displayName = new MetaverseAttribute { Name = Constants.BuiltInAttributes.DisplayName, Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var members = new MetaverseAttribute { Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        ctx.MetaverseObjectTypes.AddRange(groups, people);
        ctx.MetaverseAttributes.AddRange(displayName, members);

        var group = new MetaverseObject
        {
            Id = Guid.NewGuid(), Type = groups, Origin = MetaverseObjectOrigin.Projected, CachedDisplayName = "All Staff", ScopeReviewPending = true
        };
        ctx.MetaverseObjects.Add(group);
        ctx.Add(new MetaverseObjectAttributeValue { MetaverseObject = group, Attribute = displayName, StringValue = "All Staff" });
        for (var i = 0; i < memberCount; i++)
        {
            var person = new MetaverseObject { Id = Guid.NewGuid(), Type = people, Origin = MetaverseObjectOrigin.Projected, CachedDisplayName = $"Person {i:D2}" };
            ctx.MetaverseObjects.Add(person);
            ctx.Add(new MetaverseObjectAttributeValue { MetaverseObject = group, Attribute = members, ReferenceValue = person });
        }

        await ctx.SaveChangesAsync();
        return group.Id;
    }
}
