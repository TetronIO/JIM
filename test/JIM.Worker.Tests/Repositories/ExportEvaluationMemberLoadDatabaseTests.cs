// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage of export evaluation's member load (#2832): a target group's member attribute is loaded only
/// with the values that could name a member the page's changes add or remove, and everything else is loaded whole, each
/// value with its object, attribute and referenced object, as the whole load gives them. Opt-in via
/// <c>JIM_TEST_RESET_*</c>; ignored when absent. <c>SetUp</c> TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ExportEvaluationMemberLoadDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        var context = new JimDbContext(options);
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        return context;
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL export evaluation member load tests.");

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
    public async Task GetCsoAttributeValuesForExportEvaluationAsync_MembersAsked_LoadsOnlyTheirValuesAndEveryOtherAttributeWholeAsync()
    {
        var seeded = await SeedGroupAsync(memberCount: 40);
        var removed = seeded.Members[3];
        var notHeld = seeded.Unheld;

        await using var ctx = NewContext();
        var values = await new PostgresDataRepository(ctx).Sync.GetCsoAttributeValuesForExportEvaluationAsync([seeded.GroupId],
        [
            new ExportEvaluationMemberQuestion
            {
                ConnectedSystemObjectId = seeded.GroupId,
                AttributeId = seeded.MemberAttributeId,
                MetaverseObjectIds = new HashSet<Guid> { removed.MetaverseObjectId, notHeld.MetaverseObjectId, seeded.UnresolvedMetaverseObjectId }
            }
        ]);

        var members = values.Where(v => v.AttributeId == seeded.MemberAttributeId).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(members.Select(v => v.Id), Is.EquivalentTo(new[] { removed.ValueId, seeded.UnresolvedValueId }),
                "the member the change names, and the unresolved value whose text is an asked object's id; never the other members");
            Assert.That(members.Single(v => v.Id == removed.ValueId).ReferenceValue?.MetaverseObjectId, Is.EqualTo(removed.MetaverseObjectId),
                "a held member carries its referenced object, by which no-net-change detection matches it to the Metaverse Object");
            Assert.That(values.Where(v => v.AttributeId == seeded.DnAttributeId).Select(v => v.StringValue), Is.EqualTo(new[] { seeded.Dn }),
                "every other attribute is loaded whole");
            Assert.That(values.All(v => v.ConnectedSystemObject?.Id == seeded.GroupId && v.Attribute?.Id == v.AttributeId), Is.True,
                "each value carries its object and attribute, as the whole load's did");
        }
    }

    [Test]
    public async Task GetCsoAttributeValuesForExportEvaluationAsync_NoMemberAsked_LoadsNoneOfTheMemberAttributeAsync()
    {
        var seeded = await SeedGroupAsync(memberCount: 10);

        await using var ctx = NewContext();
        var values = await new PostgresDataRepository(ctx).Sync.GetCsoAttributeValuesForExportEvaluationAsync([seeded.GroupId],
        [
            new ExportEvaluationMemberQuestion
            {
                ConnectedSystemObjectId = seeded.GroupId,
                AttributeId = seeded.MemberAttributeId,
                MetaverseObjectIds = new HashSet<Guid>()
            }
        ]);

        Assert.That(values.Select(v => v.AttributeId), Is.EqualTo(new[] { seeded.DnAttributeId }));
    }

    [Test]
    public async Task GetCsoAttributeValuesForExportEvaluationAsync_NoQuestions_LoadsAsTheWholeLoadDoesAsync()
    {
        var seeded = await SeedGroupAsync(memberCount: 5);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).Sync;
        var lean = await repository.GetCsoAttributeValuesForExportEvaluationAsync([seeded.GroupId], []);
        var whole = await repository.GetCsoAttributeValuesByCsoIdsAsync([seeded.GroupId]);

        Assert.That(lean.Select(v => v.Id), Is.EquivalentTo(whole.Select(v => v.Id)));
    }

    #region Seeding

    private sealed record SeededMember(Guid ValueId, Guid MetaverseObjectId);

    private sealed record SeededGroup(Guid GroupId, string Dn, int DnAttributeId, int MemberAttributeId, List<SeededMember> Members,
        SeededMember Unheld, Guid UnresolvedValueId, Guid UnresolvedMetaverseObjectId);

    /// <summary>
    /// A group whose members are user objects joined to Metaverse Objects, held as resolved references; one value
    /// unresolved, whose text is a Metaverse Object's id in upper case; and a user joined to a Metaverse Object the group
    /// does not hold.
    /// </summary>
    private async Task<SeededGroup> SeedGroupAsync(int memberCount)
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var groupType = new ConnectedSystemObjectType { Name = "groupOfNames", ConnectedSystem = system, Selected = true };
        var userType = new ConnectedSystemObjectType { Name = "inetOrgPerson", ConnectedSystem = system, Selected = true };
        var dn = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsSecondaryExternalId = true
        };
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        var userDn = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName", ConnectedSystemObjectType = userType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsSecondaryExternalId = true
        };
        groupType.Attributes.Add(dn);
        groupType.Attributes.Add(member);
        userType.Attributes.Add(userDn);
        var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        seed.AddRange(connectorDefinition, system, groupType, userType, mvType);
        await seed.SaveChangesAsync();

        ConnectedSystemObject NewUser(MetaverseObject mvo) => new()
        {
            Id = Guid.NewGuid(), ConnectedSystemId = system.Id, TypeId = userType.Id, ExternalIdAttributeId = userDn.Id,
            Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.Joined, MetaverseObject = mvo
        };

        var memberMvos = Enumerable.Range(0, memberCount).Select(_ => new MetaverseObject { Type = mvType, Created = DateTime.UtcNow }).ToList();
        var unheldMvo = new MetaverseObject { Type = mvType, Created = DateTime.UtcNow };
        var unresolvedMvo = new MetaverseObject { Type = mvType, Created = DateTime.UtcNow };
        seed.MetaverseObjects.AddRange(memberMvos);
        seed.MetaverseObjects.AddRange(unheldMvo, unresolvedMvo);
        var users = memberMvos.Select(NewUser).ToList();
        seed.ConnectedSystemObjects.AddRange(users);
        seed.ConnectedSystemObjects.Add(NewUser(unheldMvo));
        await seed.SaveChangesAsync();

        var groupDn = $"cn=Group {Guid.NewGuid():N},ou=groups,dc=glitterband,dc=local";
        var group = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = system.Id, TypeId = groupType.Id, ExternalIdAttributeId = dn.Id,
            SecondaryExternalIdAttributeId = dn.Id, Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.NotJoined
        };
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = dn.Id, StringValue = groupDn });
        var members = users.Select(u => new SeededMember(Guid.NewGuid(), u.MetaverseObject!.Id)).ToList();
        foreach (var (seededMember, user) in members.Zip(users))
        {
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = seededMember.ValueId, AttributeId = member.Id, ReferenceValueId = user.Id, UnresolvedReferenceValue = $"cn={user.Id:N},ou=people"
            });
        }

        var unresolvedValueId = Guid.NewGuid();
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = unresolvedValueId, AttributeId = member.Id, UnresolvedReferenceValue = unresolvedMvo.Id.ToString().ToUpperInvariant()
        });
        seed.Add(group);
        await seed.SaveChangesAsync();

        return new SeededGroup(group.Id, groupDn, dn.Id, member.Id, members, new SeededMember(Guid.Empty, unheldMvo.Id),
            unresolvedValueId, unresolvedMvo.Id);
    }

    #endregion
}
