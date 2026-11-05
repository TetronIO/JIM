// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Seeds the shapes the history readers must resolve (#2669): live objects, deletion records keyed either way, and
/// history rows naming an id whose object has gone without the row being rewritten. The last cannot happen while the
/// history tables' foreign keys stand, so it is seeded by deleting the object with the database's referential actions
/// suspended (<see cref="PostgresTestDatabase.ExecuteWithReferentialChecksSuspendedAsync"/>).
/// </summary>
public sealed class AuditReferenceTestSeed(string connectionString, Func<JimDbContext> newContext)
{
    /// <summary>How a deletion record names the object it records.</summary>
    public enum DeletionKey
    {
        /// <summary>As deletions write them today: the subject id null, the id kept in the <c>Deleted*ObjectId</c> column.</summary>
        DeletedObjectIdColumn,

        /// <summary>As deletions will write them: the subject id itself, naming an object that no longer exists.</summary>
        SubjectId
    }

    public sealed record CsoSchema(int ConnectedSystemId, int TypeId, string TypeName, int DisplayNameAttributeId, int ExternalIdAttributeId,
        int SecondaryExternalIdAttributeId, int MemberAttributeId);

    public sealed record MvoSchema(int TypeId, int MemberAttributeId);

    // ─── Connected System Objects ───

    internal async Task<CsoSchema> SeedCsoSchemaAsync(string systemName = "Glitterband EMEA")
    {
        await using var ctx = newContext();
        var connectorDefinition = new ConnectorDefinition { Name = $"{systemName} Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = systemName, ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        ConnectedSystemObjectTypeAttribute Attribute(string name, AttributeDataType dataType, AttributePlurality plurality) => new()
        {
            Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = plurality, Selected = true
        };
        var displayName = Attribute("displayName", AttributeDataType.Text, AttributePlurality.SingleValued);
        var externalId = Attribute("employeeId", AttributeDataType.Text, AttributePlurality.SingleValued);
        externalId.IsExternalId = true;
        var secondaryExternalId = Attribute("distinguishedName", AttributeDataType.Text, AttributePlurality.SingleValued);
        secondaryExternalId.IsSecondaryExternalId = true;
        var member = Attribute("member", AttributeDataType.Reference, AttributePlurality.MultiValued);
        type.Attributes.AddRange([displayName, externalId, secondaryExternalId, member]);
        ctx.AddRange(connectorDefinition, system, type);
        await ctx.SaveChangesAsync();
        return new CsoSchema(system.Id, type.Id, type.Name, displayName.Id, externalId.Id, secondaryExternalId.Id, member.Id);
    }

    internal async Task<Guid> CreateCsoAsync(CsoSchema schema, string? displayName, string? externalId = null, string? secondaryExternalId = null)
    {
        await using var ctx = newContext();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = schema.ConnectedSystemId,
            TypeId = schema.TypeId,
            ExternalIdAttributeId = schema.ExternalIdAttributeId,
            SecondaryExternalIdAttributeId = schema.SecondaryExternalIdAttributeId,
            Created = DateTime.UtcNow
        };
        void Value(int attributeId, string? value)
        {
            if (value != null)
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, AttributeId = attributeId, StringValue = value });
        }
        Value(schema.DisplayNameAttributeId, displayName);
        Value(schema.ExternalIdAttributeId, externalId);
        Value(schema.SecondaryExternalIdAttributeId, secondaryExternalId);
        ctx.Add(cso);
        await ctx.SaveChangesAsync();
        return cso.Id;
    }

    /// <summary>
    /// Writes a Connected System Object deletion record for <paramref name="objectId"/>, which need not exist, keyed as
    /// <paramref name="key"/> says. Returns the record's id.
    /// </summary>
    internal async Task<Guid> CreateCsoDeletionRecordAsync(CsoSchema schema, Guid objectId, DeletionKey key, DateTime changeTime,
        string? displayName, string? externalId)
    {
        var change = new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = schema.ConnectedSystemId,
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = changeTime,
            DeletedObjectDisplayName = displayName,
            DeletedObjectExternalId = externalId,
            DeletedConnectedSystemObjectId = key == DeletionKey.DeletedObjectIdColumn ? objectId : null
        };
        await using (var ctx = newContext())
        {
            ctx.Add(change);
            ctx.Entry(change).Property("DeletedObjectTypeId").CurrentValue = schema.TypeId;
            await ctx.SaveChangesAsync();
        }

        if (key == DeletionKey.SubjectId)
            await SetDanglingAsync("""UPDATE "ConnectedSystemObjectChanges" SET "ConnectedSystemObjectId" = @object WHERE "Id" = @row""", change.Id, objectId);
        return change.Id;
    }

    /// <summary>
    /// Writes a change record of <paramref name="changeType"/> naming <paramref name="objectId"/> by its subject id,
    /// which need not name an existing object. Returns the record's id.
    /// </summary>
    internal async Task<Guid> CreateCsoChangeAsync(CsoSchema schema, Guid objectId, ObjectChangeType changeType, DateTime changeTime,
        string? deletedObjectExternalId = null)
    {
        var change = new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = schema.ConnectedSystemId,
            ChangeType = changeType,
            ChangeTime = changeTime,
            DeletedObjectExternalId = deletedObjectExternalId
        };
        await using (var ctx = newContext())
        {
            ctx.Add(change);
            await ctx.SaveChangesAsync();
        }

        await SetDanglingAsync("""UPDATE "ConnectedSystemObjectChanges" SET "ConnectedSystemObjectId" = @object WHERE "Id" = @row""", change.Id, objectId);
        return change.Id;
    }

    /// <summary>
    /// Deletes a Connected System Object and its values without touching any row that names it, as deletion will once
    /// history keeps its ids: every such row is left naming an id with nothing behind it.
    /// </summary>
    internal Task DeleteCsoLeavingHistoryAsync(Guid objectId) => PostgresTestDatabase.ExecuteWithReferentialChecksSuspendedAsync(
        connectionString,
        """
        DELETE FROM "ConnectedSystemObjectAttributeValues" WHERE "ConnectedSystemObjectId" = @object;
        DELETE FROM "ConnectedSystemObjects" WHERE "Id" = @object;
        """,
        new Dictionary<string, object> { ["object"] = objectId });

    // ─── Metaverse Objects ───

    internal async Task<MvoSchema> SeedMvoSchemaAsync()
    {
        await using var ctx = newContext();
        var type = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var member = new MetaverseAttribute { Name = "directReports", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        ctx.MetaverseObjectTypes.Add(type);
        ctx.MetaverseAttributes.Add(member);
        await ctx.SaveChangesAsync();
        return new MvoSchema(type.Id, member.Id);
    }

    internal async Task<Guid> CreateMvoAsync(MvoSchema schema, string? displayName)
    {
        await using var ctx = newContext();
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Origin = MetaverseObjectOrigin.Projected, CachedDisplayName = displayName };
        ctx.Add(mvo);
        ctx.Entry(mvo).Property("TypeId").CurrentValue = schema.TypeId;
        await ctx.SaveChangesAsync();
        return mvo.Id;
    }

    /// <summary>
    /// Writes a Metaverse Object deletion record for <paramref name="objectId"/>, which need not exist, keyed as
    /// <paramref name="key"/> says. Returns the record's id.
    /// </summary>
    internal async Task<Guid> CreateMvoDeletionRecordAsync(MvoSchema schema, Guid objectId, DeletionKey key, DateTime changeTime, string? displayName)
    {
        var change = new MetaverseObjectChange
        {
            Id = Guid.NewGuid(),
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = changeTime,
            DeletedObjectTypeId = schema.TypeId,
            DeletedObjectDisplayName = displayName,
            DeletedMetaverseObjectId = key == DeletionKey.DeletedObjectIdColumn ? objectId : null
        };
        await using (var ctx = newContext())
        {
            ctx.Add(change);
            await ctx.SaveChangesAsync();
        }

        if (key == DeletionKey.SubjectId)
            await SetDanglingAsync("""UPDATE "MetaverseObjectChanges" SET "MetaverseObjectId" = @object WHERE "Id" = @row""", change.Id, objectId);
        return change.Id;
    }

    /// <summary>
    /// Writes an update change record naming <paramref name="objectId"/> by its subject id, which need not name an
    /// existing object. Returns the record's id.
    /// </summary>
    internal async Task<Guid> CreateMvoChangeAsync(Guid objectId, DateTime changeTime)
    {
        var change = new MetaverseObjectChange { Id = Guid.NewGuid(), ChangeType = ObjectChangeType.Updated, ChangeTime = changeTime };
        await using (var ctx = newContext())
        {
            ctx.Add(change);
            await ctx.SaveChangesAsync();
        }

        await SetDanglingAsync("""UPDATE "MetaverseObjectChanges" SET "MetaverseObjectId" = @object WHERE "Id" = @row""", change.Id, objectId);
        return change.Id;
    }

    /// <summary>
    /// Deletes a Metaverse Object and its values without touching any row that names it, as deletion will once history
    /// keeps its ids.
    /// </summary>
    internal Task DeleteMvoLeavingHistoryAsync(Guid objectId) => PostgresTestDatabase.ExecuteWithReferentialChecksSuspendedAsync(
        connectionString,
        """
        DELETE FROM "MetaverseObjectAttributeValues" WHERE "MetaverseObjectId" = @object;
        DELETE FROM "MetaverseObjects" WHERE "Id" = @object;
        """,
        new Dictionary<string, object> { ["object"] = objectId });

    private Task SetDanglingAsync(string sql, Guid rowId, Guid objectId) => PostgresTestDatabase.ExecuteWithReferentialChecksSuspendedAsync(
        connectionString, sql, new Dictionary<string, object> { ["row"] = rowId, ["object"] = objectId });
}
