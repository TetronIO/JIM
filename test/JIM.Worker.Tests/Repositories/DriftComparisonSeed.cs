// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using Npgsql;
using NpgsqlTypes;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// #2818: a Target Connected System of users and groups, each object optionally joined to its own Metaverse Object, the
/// groups' member values given row by row. Written in SQL, so a group of tens of thousands of members costs a moment.
/// </summary>
internal sealed class DriftComparisonSeed
{
    private readonly string _connectionString;

    private DriftComparisonSeed(string connectionString) => _connectionString = connectionString;

    public int SystemId { get; private init; }
    public int UserTypeId { get; private init; }
    public int GroupTypeId { get; private init; }
    public int UserExternalIdAttributeId { get; private init; }
    public int GroupExternalIdAttributeId { get; private init; }
    public int MemberAttributeId { get; private init; }
    public ConnectedSystemObjectTypeAttribute MemberAttribute { get; private init; } = null!;
    public int PersonTypeId { get; private init; }
    public int GroupMvTypeId { get; private init; }
    public int StaticMembersAttributeId { get; private init; }

    /// <summary>One member value of a group, as a row.</summary>
    public sealed record MemberRow(Guid Id, Guid? ReferenceValueId = null, string? UnresolvedReferenceValue = null, string? StringValue = null, int? IntValue = null);

    public static async Task<DriftComparisonSeed> CreateAsync(Func<JimDbContext> newContext, string connectionString)
    {
        await using var seed = newContext();
        var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Target", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule };
        var userType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var userExternalId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = userType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        userType.Attributes.Add(userExternalId);
        var groupType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
        var groupExternalId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        groupType.Attributes.Add(groupExternalId);
        groupType.Attributes.Add(member);

        var personType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var groupMvType = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
        var staticMembers = new MetaverseAttribute
        {
            Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued,
            MetaverseObjectTypes = [groupMvType]
        };

        seed.AddRange(connectorDefinition, system, userType, groupType, personType, groupMvType, staticMembers);
        await seed.SaveChangesAsync();

        return new DriftComparisonSeed(connectionString)
        {
            SystemId = system.Id, UserTypeId = userType.Id, GroupTypeId = groupType.Id,
            UserExternalIdAttributeId = userExternalId.Id, GroupExternalIdAttributeId = groupExternalId.Id,
            MemberAttributeId = member.Id, MemberAttribute = member,
            PersonTypeId = personType.Id, GroupMvTypeId = groupMvType.Id, StaticMembersAttributeId = staticMembers.Id
        };
    }

    /// <summary>
    /// Users, each with its External ID value, joined to its own new Person where <c>MvoId</c> is given.
    /// </summary>
    public async Task AddUsersAsync(IReadOnlyList<(Guid CsoId, Guid? MvoId)> users) =>
        await AddObjectsAsync(users.Select(u => (u.CsoId, u.MvoId, ConnectedSystemObjectStatus.Normal)).ToList(), UserTypeId, PersonTypeId,
            UserExternalIdAttributeId, synchronisationPending: false);

    /// <summary>
    /// Users in the given states, marked pending synchronisation or not.
    /// </summary>
    public async Task AddUsersAsync(IReadOnlyList<(Guid CsoId, Guid? MvoId, ConnectedSystemObjectStatus Status)> users, bool synchronisationPending) =>
        await AddObjectsAsync(users, UserTypeId, PersonTypeId, UserExternalIdAttributeId, synchronisationPending);

    /// <summary>
    /// Groups, each with its External ID value, joined to its own new Group Metaverse Object where <c>MvoId</c> is given,
    /// and marked pending synchronisation.
    /// </summary>
    public async Task AddGroupsAsync(IReadOnlyList<(Guid CsoId, Guid? MvoId, ConnectedSystemObjectStatus Status)> groups) =>
        await AddObjectsAsync(groups, GroupTypeId, GroupMvTypeId, GroupExternalIdAttributeId, synchronisationPending: true);

    /// <summary>
    /// A group's member values, row by row.
    /// </summary>
    public async Task AddMemberRowsAsync(Guid groupCsoId, IReadOnlyList<MemberRow> rows)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "ConnectedSystemObjectId", "AttributeId", "ReferenceValueId",
                                                                "UnresolvedReferenceValue", "StringValue", "IntValue")
            SELECT r.id, @cso, @attribute, r.ref, r.unresolved, r.text, r.number
            FROM unnest(@ids, @refs, @unresolved, @texts, @numbers) AS r(id, ref, unresolved, text, number)
            """, connection);
        command.Parameters.AddWithValue("cso", groupCsoId);
        command.Parameters.AddWithValue("attribute", MemberAttributeId);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(r => r.Id).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("refs", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(r => (object?)r.ReferenceValueId ?? DBNull.Value).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("unresolved", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => (object?)r.UnresolvedReferenceValue ?? DBNull.Value).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("texts", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => (object?)r.StringValue ?? DBNull.Value).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("numbers", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = rows.Select(r => (object?)r.IntValue ?? DBNull.Value).ToArray() });
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Values of the group Metaverse Object's Static Members, one per referenced Metaverse Object.
    /// </summary>
    public async Task AddStaticMembersAsync(Guid groupMvoId, IReadOnlyList<Guid> memberMvoIds)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO "MetaverseObjectAttributeValues" ("Id", "MetaverseObjectId", "AttributeId", "ReferenceValueId", "ContributedBySystemId")
            SELECT md5(@mvo::text || m::text || o::text)::uuid, @mvo, @attribute, m, @system FROM unnest(@members) WITH ORDINALITY AS u(m, o)
            """, connection);
        command.Parameters.AddWithValue("mvo", groupMvoId);
        command.Parameters.AddWithValue("attribute", StaticMembersAttributeId);
        command.Parameters.AddWithValue("system", SystemId);
        command.Parameters.Add(new NpgsqlParameter("members", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = memberMvoIds.ToArray() });
        await command.ExecuteNonQueryAsync();
    }

    public async Task AnalyseAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            ANALYZE "MetaverseObjects"; ANALYZE "MetaverseObjectAttributeValues";
            ANALYZE "ConnectedSystemObjects"; ANALYZE "ConnectedSystemObjectAttributeValues";
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task AddObjectsAsync(IReadOnlyList<(Guid CsoId, Guid? MvoId, ConnectedSystemObjectStatus Status)> objects, int csoTypeId, int mvoTypeId,
        int externalIdAttributeId, bool synchronisationPending)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO "MetaverseObjects" ("Id", "Created", "TypeId", "Status", "DeletionInitiatedByType", "Origin")
                SELECT o.mvo, now() - interval '1 day', @mvoType, 0, 0, 0 FROM unnest(@csos, @mvos, @statuses) AS o(cso, mvo, status) WHERE o.mvo IS NOT NULL;
            INSERT INTO "ConnectedSystemObjects" ("Id", "Created", "TypeId", "ConnectedSystemId", "ExternalIdAttributeId", "Status",
                                                  "JoinType", "MetaverseObjectId", "SynchronisationPending")
                SELECT o.cso, now() - interval '1 day', @csoType, @system, @externalId, o.status,
                       CASE WHEN o.mvo IS NULL THEN 0 ELSE 1 END, o.mvo, @pending
                FROM unnest(@csos, @mvos, @statuses) AS o(cso, mvo, status);
            INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "ConnectedSystemObjectId", "AttributeId", "StringValue")
                SELECT md5(o.cso::text)::uuid, o.cso, @externalId, o.cso::text FROM unnest(@csos) AS o(cso);
            """, connection);
        command.Parameters.AddWithValue("mvoType", mvoTypeId);
        command.Parameters.AddWithValue("csoType", csoTypeId);
        command.Parameters.AddWithValue("system", SystemId);
        command.Parameters.AddWithValue("externalId", externalIdAttributeId);
        command.Parameters.AddWithValue("pending", synchronisationPending);
        command.Parameters.Add(new NpgsqlParameter("csos", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = objects.Select(o => o.CsoId).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("mvos", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = objects.Select(o => (object?)o.MvoId ?? DBNull.Value).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("statuses", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = objects.Select(o => (int)o.Status).ToArray() });
        await command.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }
}
