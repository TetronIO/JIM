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
/// Real-PostgreSQL verification of the page-batched reference external ID lookup
/// (<c>GetReferenceExternalIdsForCsosAsync</c>).
/// </summary>
/// <remarks>
/// The Scale500k25kGroups confirming import issued the single-CSO variant
/// (<c>GetReferenceExternalIdsAsync</c>) once per existing Connected System Object: 535,425
/// individual database round trips (measured via pg_stat_statements, 2026-07-20). The batched
/// variant fetches a whole hydration page's lookups in one query. These tests lock in parity
/// with the single-CSO variant: per-owner grouping, secondary-over-primary external ID
/// preference, exclusion of unresolved references and of referenced CSOs with no external ID
/// value. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other
/// <c>RequiresPostgres</c> fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ReferenceExternalIdBatchLookupDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL reference external ID batch lookup tests.");

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

    private sealed record BatchLookupSeed(
        Guid Group1Id,
        Guid Group2Id,
        Guid UnrequestedGroupId,
        Guid MemberWithBothIdsId,
        Guid MemberWithPrimaryOnlyId,
        Guid MemberWithNoExternalIdValueId);

    /// <summary>
    /// Seeds a directory-shaped graph: three group CSOs whose member attribute references three
    /// user CSOs with different external ID configurations, plus lookup-noise the query must
    /// ignore (an unresolved reference value and a non-reference attribute value).
    /// </summary>
    private async Task<BatchLookupSeed> SeedGroupsWithMembersAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };

        var userType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var entryUuidAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "entryUUID", ConnectedSystemObjectType = userType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var dnAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "dn", ConnectedSystemObjectType = userType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        userType.Attributes.Add(entryUuidAttr);
        userType.Attributes.Add(dnAttr);

        var groupType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        groupType.Attributes.Add(memberAttr);

        seed.AddRange(connectorDefinition, system, userType, groupType);
        await seed.SaveChangesAsync();

        // Member with both primary (entryUUID) and secondary (dn) external ID values: the
        // lookup must prefer the secondary value, matching the single-CSO variant's COALESCE.
        var memberWithBothIds = new ConnectedSystemObject
        {
            Type = userType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = entryUuidAttr.Id, SecondaryExternalIdAttributeId = dnAttr.Id
        };
        memberWithBothIds.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = entryUuidAttr, AttributeId = entryUuidAttr.Id, StringValue = "uuid-member-1"
        });
        memberWithBothIds.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = dnAttr, AttributeId = dnAttr.Id, StringValue = "cn=member1,dc=example,dc=com"
        });

        // Member with only a primary external ID value: the lookup must fall back to it.
        var memberWithPrimaryOnly = new ConnectedSystemObject
        {
            Type = userType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = entryUuidAttr.Id, SecondaryExternalIdAttributeId = dnAttr.Id
        };
        memberWithPrimaryOnly.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = entryUuidAttr, AttributeId = entryUuidAttr.Id, StringValue = "uuid-member-2"
        });

        // Member with no external ID values at all: referenced, but must not appear in results.
        var memberWithNoExternalIdValue = new ConnectedSystemObject
        {
            Type = userType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = entryUuidAttr.Id, SecondaryExternalIdAttributeId = dnAttr.Id
        };

        // Group 1 references all three members, plus noise the query must ignore: an
        // unresolved (string-only) reference value and a non-reference attribute value.
        var group1 = new ConnectedSystemObject
        {
            Type = groupType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = entryUuidAttr.Id
        };
        group1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = memberAttr, AttributeId = memberAttr.Id, ReferenceValue = memberWithBothIds
        });
        group1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = memberAttr, AttributeId = memberAttr.Id, ReferenceValue = memberWithPrimaryOnly
        });
        group1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = memberAttr, AttributeId = memberAttr.Id, ReferenceValue = memberWithNoExternalIdValue
        });
        group1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = memberAttr, AttributeId = memberAttr.Id,
            UnresolvedReferenceValue = "cn=unresolved,dc=example,dc=com"
        });
        group1.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = entryUuidAttr, AttributeId = entryUuidAttr.Id, StringValue = "uuid-group-1"
        });

        // Group 2 references a single member, proving results are grouped per owning CSO.
        var group2 = new ConnectedSystemObject
        {
            Type = groupType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = entryUuidAttr.Id
        };
        group2.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = memberAttr, AttributeId = memberAttr.Id, ReferenceValue = memberWithPrimaryOnly
        });

        // A group that will not be in the requested ID set: must not appear in results.
        var unrequestedGroup = new ConnectedSystemObject
        {
            Type = groupType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = entryUuidAttr.Id
        };
        unrequestedGroup.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = memberAttr, AttributeId = memberAttr.Id, ReferenceValue = memberWithBothIds
        });

        seed.AddRange(memberWithBothIds, memberWithPrimaryOnly, memberWithNoExternalIdValue, group1, group2, unrequestedGroup);
        await seed.SaveChangesAsync();

        return new BatchLookupSeed(
            group1.Id, group2.Id, unrequestedGroup.Id,
            memberWithBothIds.Id, memberWithPrimaryOnly.Id, memberWithNoExternalIdValue.Id);
    }

    [Test]
    public async Task GetReferenceExternalIdsForCsosAsync_MultipleOwners_GroupsPerOwnerWithSingleCsoParityAsync()
    {
        // Arrange
        var seeded = await SeedGroupsWithMembersAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Act - include a CSO with no reference values (a user) to prove the every-requested-ID contract
        var batched = await repository.Sync.GetReferenceExternalIdsForCsosAsync(
            new[] { seeded.Group1Id, seeded.Group2Id, seeded.MemberWithBothIdsId });

        // Assert: every requested owner appears and only requested owners appear
        Assert.That(batched.Keys, Is.EquivalentTo(new[] { seeded.Group1Id, seeded.Group2Id, seeded.MemberWithBothIdsId }));
        Assert.That(batched[seeded.MemberWithBothIdsId], Is.Empty,
            "A requested CSO with no resolved reference values must map to an empty dictionary");

        // Group 1: secondary external ID preferred, primary as fallback, no-value member excluded
        var group1Refs = batched[seeded.Group1Id];
        Assert.That(group1Refs, Has.Count.EqualTo(2));
        Assert.That(group1Refs[seeded.MemberWithBothIdsId], Is.EqualTo("cn=member1,dc=example,dc=com"),
            "Secondary external ID (dn) must be preferred over the primary (entryUUID)");
        Assert.That(group1Refs[seeded.MemberWithPrimaryOnlyId], Is.EqualTo("uuid-member-2"),
            "Primary external ID must be used when no secondary value exists");
        Assert.That(group1Refs.ContainsKey(seeded.MemberWithNoExternalIdValueId), Is.False,
            "A referenced CSO with no external ID values must not appear");

        // Group 2: independent, smaller dictionary
        var group2Refs = batched[seeded.Group2Id];
        Assert.That(group2Refs, Has.Count.EqualTo(1));
        Assert.That(group2Refs[seeded.MemberWithPrimaryOnlyId], Is.EqualTo("uuid-member-2"));

        // Parity: each per-owner dictionary must equal what the single-CSO variant returns
        foreach (var (csoId, batchedRefs) in batched)
        {
            var single = await repository.Sync.GetReferenceExternalIdsAsync(csoId);
            Assert.That(batchedRefs, Is.EquivalentTo(single),
                $"Batched lookup for CSO {csoId} must match the single-CSO variant");
        }
    }

    private sealed record SharedReferencesSeed(
        Guid GroupAId,
        Guid GroupBId,
        Guid GroupCId,
        Guid UserWithDnId,
        Guid UserWithoutDnId,
        Guid UserWithNoExternalIdValueId,
        Guid OtherUserWithDnId,
        int MemberAttributeId,
        int ManagedByAttributeId);

    /// <summary>
    /// #2521: a page of groups sharing their members, as the confirming Full Import's first page is (the 100 largest groups
    /// hold 3 million references to 495,000 users). Three groups reference the same users through two reference attributes
    /// (member, and managedBy). One user has a Secondary External ID (a DN), one only an External ID, one neither. Group A
    /// references a user through both attributes, and holds an unresolved reference and a text value besides.
    /// </summary>
    private async Task<SharedReferencesSeed> SeedGroupsSharingMembersAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone", ConnectorDefinition = connectorDefinition };

        var userType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var entryUuidAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "entryUUID", ConnectedSystemObjectType = userType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var dnAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "dn", ConnectedSystemObjectType = userType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        userType.Attributes.Add(entryUuidAttr);
        userType.Attributes.Add(dnAttr);

        var groupType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        var managedByAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "managedBy", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var descriptionAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "description", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        groupType.Attributes.Add(memberAttr);
        groupType.Attributes.Add(managedByAttr);
        groupType.Attributes.Add(descriptionAttr);

        seed.AddRange(connectorDefinition, system, userType, groupType);
        await seed.SaveChangesAsync();

        ConnectedSystemObject User(string entryUuid, string? dn)
        {
            var user = new ConnectedSystemObject
            {
                Type = userType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
                ExternalIdAttributeId = entryUuidAttr.Id, SecondaryExternalIdAttributeId = dnAttr.Id
            };
            if (entryUuid.Length > 0)
                user.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = entryUuidAttr, AttributeId = entryUuidAttr.Id, StringValue = entryUuid });
            if (dn != null)
                user.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = dnAttr, AttributeId = dnAttr.Id, StringValue = dn });
            return user;
        }

        var userWithDn = User("uuid-ada", "cn=Ada,ou=Users,dc=yellowstone,dc=local");
        var userWithoutDn = User("uuid-grace", null);
        var userWithNoExternalIdValue = User(string.Empty, null);
        var otherUserWithDn = User("uuid-alan", "cn=Alan,ou=Users,dc=yellowstone,dc=local");

        ConnectedSystemObject Group(params (ConnectedSystemObjectTypeAttribute Attribute, ConnectedSystemObject? Reference, string? Unresolved)[] values)
        {
            var group = new ConnectedSystemObject
            {
                Type = groupType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
                ExternalIdAttributeId = entryUuidAttr.Id
            };
            foreach (var (attribute, reference, unresolved) in values)
            {
                group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    Attribute = attribute, AttributeId = attribute.Id, ReferenceValue = reference, UnresolvedReferenceValue = unresolved
                });
            }

            return group;
        }

        var groupA = Group(
            (memberAttr, userWithDn, null),
            (memberAttr, userWithoutDn, null),
            (memberAttr, userWithNoExternalIdValue, null),
            (memberAttr, null, "cn=Nobody,ou=Users,dc=yellowstone,dc=local"),
            (managedByAttr, userWithDn, null));
        groupA.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = descriptionAttr, AttributeId = descriptionAttr.Id, StringValue = "Not a reference"
        });
        var groupB = Group(
            (memberAttr, userWithDn, null),
            (memberAttr, userWithoutDn, null),
            (managedByAttr, otherUserWithDn, null));
        var groupC = Group(
            (memberAttr, userWithDn, null),
            (memberAttr, otherUserWithDn, null));

        seed.AddRange(userWithDn, userWithoutDn, userWithNoExternalIdValue, otherUserWithDn, groupA, groupB, groupC);
        await seed.SaveChangesAsync();

        return new SharedReferencesSeed(groupA.Id, groupB.Id, groupC.Id, userWithDn.Id, userWithoutDn.Id, userWithNoExternalIdValue.Id,
            otherUserWithDn.Id, memberAttr.Id, managedByAttr.Id);
    }

    /// <summary>
    /// #2521: what the lookup answers for a page of groups sharing their members, pinned so that a rewrite of the statement
    /// returns exactly what it returned before: per group, each object it references through any attribute not left out,
    /// by its Secondary External ID where it has one and otherwise its External ID; nothing for an object with neither, an
    /// unresolved reference or a value that is not a reference.
    /// </summary>
    [Test]
    public async Task GetReferenceExternalIdsForCsosAsync_GroupsSharingMembers_ReturnsEachGroupsReferencesAsync()
    {
        var seeded = await SeedGroupsSharingMembersAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var whole = await repository.Sync.GetReferenceExternalIdsForCsosAsync([seeded.GroupAId, seeded.GroupBId, seeded.GroupCId, seeded.UserWithDnId]);
        var leavingMembersOut = await repository.Sync.GetReferenceExternalIdsForCsosAsync([seeded.GroupAId, seeded.GroupBId, seeded.GroupCId],
            [seeded.MemberAttributeId]);
        var leavingManagersOut = await repository.Sync.GetReferenceExternalIdsForCsosAsync([seeded.GroupAId, seeded.GroupBId, seeded.GroupCId],
            [seeded.ManagedByAttributeId]);

        const string ada = "cn=Ada,ou=Users,dc=yellowstone,dc=local";
        const string alan = "cn=Alan,ou=Users,dc=yellowstone,dc=local";
        const string grace = "uuid-grace";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(whole.Keys, Is.EquivalentTo(new[] { seeded.GroupAId, seeded.GroupBId, seeded.GroupCId, seeded.UserWithDnId }));
            Assert.That(whole[seeded.GroupAId], Is.EquivalentTo(new Dictionary<Guid, string> { [seeded.UserWithDnId] = ada, [seeded.UserWithoutDnId] = grace }));
            Assert.That(whole[seeded.GroupBId], Is.EquivalentTo(new Dictionary<Guid, string>
                { [seeded.UserWithDnId] = ada, [seeded.UserWithoutDnId] = grace, [seeded.OtherUserWithDnId] = alan }));
            Assert.That(whole[seeded.GroupCId], Is.EquivalentTo(new Dictionary<Guid, string> { [seeded.UserWithDnId] = ada, [seeded.OtherUserWithDnId] = alan }));
            Assert.That(whole[seeded.UserWithDnId], Is.Empty);

            Assert.That(leavingMembersOut[seeded.GroupAId], Is.EquivalentTo(new Dictionary<Guid, string> { [seeded.UserWithDnId] = ada }));
            Assert.That(leavingMembersOut[seeded.GroupBId], Is.EquivalentTo(new Dictionary<Guid, string> { [seeded.OtherUserWithDnId] = alan }));
            Assert.That(leavingMembersOut[seeded.GroupCId], Is.Empty);

            Assert.That(leavingManagersOut[seeded.GroupAId], Is.EquivalentTo(whole[seeded.GroupAId]), "Group A references Ada as a member too");
            Assert.That(leavingManagersOut[seeded.GroupBId], Is.EquivalentTo(new Dictionary<Guid, string> { [seeded.UserWithDnId] = ada, [seeded.UserWithoutDnId] = grace }));
            Assert.That(leavingManagersOut[seeded.GroupCId], Is.EquivalentTo(whole[seeded.GroupCId]));
        }

        foreach (var (csoId, batched) in whole)
            Assert.That(batched, Is.EquivalentTo(await repository.Sync.GetReferenceExternalIdsAsync(csoId)), $"CSO {csoId}: parity with the single-object lookup");
    }

    [Test]
    public async Task GetReferenceExternalIdsForCsosAsync_EmptyInput_ReturnsEmptyAsync()
    {
        // Arrange
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Act
        var result = await repository.Sync.GetReferenceExternalIdsForCsosAsync(Array.Empty<Guid>());

        // Assert
        Assert.That(result, Is.Empty);
    }
}
