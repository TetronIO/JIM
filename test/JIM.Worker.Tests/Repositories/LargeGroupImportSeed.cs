// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using Npgsql;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// #2817: a Connected System holding a directory's users and one group whose member attribute holds a resolved
/// reference to each of them, keeping each member's DN as resolution leaves it. Seeded in SQL, copied from rows written
/// through EF, so a group of hundreds of thousands of members costs seconds and every other column takes a valid value.
/// </summary>
internal sealed class LargeGroupImportSeed
{
    public int SystemId { get; private init; }
    public int UserTypeId { get; private init; }
    public int GroupTypeId { get; private init; }
    public int UserDnAttributeId { get; private init; }
    public int UserGuidAttributeId { get; private init; }
    public int GroupDnAttributeId { get; private init; }
    public int GroupGuidAttributeId { get; private init; }
    public int MemberAttributeId { get; private init; }
    public Guid GroupCsoId { get; private init; }
    public int MemberCount { get; private init; }

    /// <summary>The DN user <paramref name="n"/> holds, and the group's member value naming it.</summary>
    public static string UserDn(int n) => $"uid=user{n},ou=People,dc=glitterband,dc=local";

    /// <summary>The id of the Connected System Object of user <paramref name="n"/>.</summary>
    public static Guid UserCsoId(int n) => DeterministicGuid("user" + n);

    /// <summary>The id of the group's member value naming user <paramref name="n"/>.</summary>
    public static Guid MemberValueId(int n) => DeterministicGuid("member" + n);

    /// <summary>
    /// The group's DN, which is also the value its import object carries as its Secondary External ID.
    /// </summary>
    public const string GroupDn = "cn=AllStaff,ou=Groups,dc=glitterband,dc=local";

    /// <summary>The group's External ID value.</summary>
    public static readonly Guid GroupGuid = new("6a4f3c3e-0b75-4c1b-9d55-0d7c2c1b0817");

    public static async Task<LargeGroupImportSeed> SeedAsync(Func<JimDbContext> newContext, string connectionString, int userCount, int memberCount)
    {
        int systemId, userTypeId, groupTypeId, userDnId, userGuidId, groupDnId, groupGuidId, memberId;
        Guid groupCsoId;
        await using (var seed = newContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Glitterband LDAP", ConnectorDefinition = connectorDefinition };
            var userType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var userGuid = Attribute(userType, "objectGUID", AttributeDataType.Guid, AttributePlurality.SingleValued, externalId: true);
            var userDn = Attribute(userType, "distinguishedName", AttributeDataType.Text, AttributePlurality.SingleValued, secondaryExternalId: true);
            var groupType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
            var groupGuid = Attribute(groupType, "objectGUID", AttributeDataType.Guid, AttributePlurality.SingleValued, externalId: true);
            var groupDn = Attribute(groupType, "distinguishedName", AttributeDataType.Text, AttributePlurality.SingleValued, secondaryExternalId: true);
            var member = Attribute(groupType, "member", AttributeDataType.Reference, AttributePlurality.MultiValued);
            seed.AddRange(connectorDefinition, system, userType, groupType);
            await seed.SaveChangesAsync();

            // One user and the group, written through EF; the SQL below copies them.
            var templateUser = new ConnectedSystemObject
            {
                Id = UserCsoId(0), Type = userType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
                ExternalIdAttributeId = userGuid.Id, SecondaryExternalIdAttributeId = userDn.Id
            };
            templateUser.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                { Id = DeterministicGuid("guid0"), ConnectedSystemObject = templateUser, Attribute = userGuid, GuidValue = DeterministicGuid("objectGUID0") });
            templateUser.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                { Id = DeterministicGuid("dn0"), ConnectedSystemObject = templateUser, Attribute = userDn, StringValue = UserDn(0) });

            var group = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(), Type = groupType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
                ExternalIdAttributeId = groupGuid.Id, SecondaryExternalIdAttributeId = groupDn.Id
            };
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                { Id = Guid.NewGuid(), ConnectedSystemObject = group, Attribute = groupGuid, GuidValue = GroupGuid });
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                { Id = Guid.NewGuid(), ConnectedSystemObject = group, Attribute = groupDn, StringValue = GroupDn });
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = MemberValueId(0), ConnectedSystemObject = group, Attribute = member,
                ReferenceValue = templateUser, UnresolvedReferenceValue = UserDn(0)
            });
            seed.AddRange(templateUser, group);
            await seed.SaveChangesAsync();

            (systemId, userTypeId, groupTypeId) = (system.Id, userType.Id, groupType.Id);
            (userDnId, userGuidId, groupDnId, groupGuidId, memberId) = (userDn.Id, userGuid.Id, groupDn.Id, groupGuid.Id, member.Id);
            groupCsoId = group.Id;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var populate = new NpgsqlCommand("""
            INSERT INTO "ConnectedSystemObjects"
            SELECT (jsonb_populate_record(cso, jsonb_build_object('Id', md5('user' || n)::uuid))).*
            FROM "ConnectedSystemObjects" AS cso, generate_series(1, @userCount - 1) AS n
            WHERE cso."Id" = @templateUserId;

            INSERT INTO "ConnectedSystemObjectAttributeValues"
            SELECT (jsonb_populate_record(av, jsonb_build_object(
                'Id', md5('dn' || n)::uuid,
                'ConnectedSystemObjectId', md5('user' || n)::uuid,
                'StringValue', 'uid=user' || n || ',ou=People,dc=glitterband,dc=local'))).*
            FROM "ConnectedSystemObjectAttributeValues" AS av, generate_series(1, @userCount - 1) AS n
            WHERE av."ConnectedSystemObjectId" = @templateUserId AND av."AttributeId" = @userDnId;

            INSERT INTO "ConnectedSystemObjectAttributeValues"
            SELECT (jsonb_populate_record(av, jsonb_build_object(
                'Id', md5('guid' || n)::uuid,
                'ConnectedSystemObjectId', md5('user' || n)::uuid,
                'GuidValue', md5('objectGUID' || n)::uuid))).*
            FROM "ConnectedSystemObjectAttributeValues" AS av, generate_series(1, @userCount - 1) AS n
            WHERE av."ConnectedSystemObjectId" = @templateUserId AND av."AttributeId" = @userGuidId;

            INSERT INTO "ConnectedSystemObjectAttributeValues"
            SELECT (jsonb_populate_record(av, jsonb_build_object(
                'Id', md5('member' || n)::uuid,
                'ReferenceValueId', md5('user' || n)::uuid,
                'UnresolvedReferenceValue', 'uid=user' || n || ',ou=People,dc=glitterband,dc=local'))).*
            FROM "ConnectedSystemObjectAttributeValues" AS av, generate_series(1, @memberCount - 1) AS n
            WHERE av."ConnectedSystemObjectId" = @groupCsoId AND av."AttributeId" = @memberId;

            ANALYZE "ConnectedSystemObjects";
            ANALYZE "ConnectedSystemObjectAttributeValues";
            """, connection))
        {
            populate.CommandTimeout = 600;
            populate.Parameters.AddWithValue("userCount", userCount);
            populate.Parameters.AddWithValue("memberCount", memberCount);
            populate.Parameters.AddWithValue("templateUserId", UserCsoId(0));
            populate.Parameters.AddWithValue("userDnId", userDnId);
            populate.Parameters.AddWithValue("userGuidId", userGuidId);
            populate.Parameters.AddWithValue("groupCsoId", groupCsoId);
            populate.Parameters.AddWithValue("memberId", memberId);
            await populate.ExecuteNonQueryAsync();
        }

        return new LargeGroupImportSeed
        {
            SystemId = systemId, UserTypeId = userTypeId, GroupTypeId = groupTypeId,
            UserDnAttributeId = userDnId, UserGuidAttributeId = userGuidId,
            GroupDnAttributeId = groupDnId, GroupGuidAttributeId = groupGuidId, MemberAttributeId = memberId,
            GroupCsoId = groupCsoId, MemberCount = memberCount
        };
    }

    /// <summary>The same id PostgreSQL's <c>md5(text)::uuid</c> gives, so .NET and the seeding SQL agree.</summary>
    public static Guid DeterministicGuid(string text)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Guid.Parse(Convert.ToHexString(hash));
    }

    /// <summary>PostgreSQL's <c>md5(id::text)::uuid</c> of an id.</summary>
    public static Guid DeterministicGuidOf(Guid id) => DeterministicGuid(id.ToString());

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType type, string name, AttributeDataType dataType,
        AttributePlurality plurality, bool externalId = false, bool secondaryExternalId = false)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = plurality,
            IsExternalId = externalId, IsSecondaryExternalId = secondaryExternalId, Selected = true
        };
        type.Attributes.Add(attribute);
        return attribute;
    }
}
