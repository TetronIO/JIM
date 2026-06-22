// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Serilog;
namespace JIM.PostgresData.Repositories;

internal sealed class SystemRepository : ISystemRepository
{
    private readonly PostgresDataRepository _repository;

    public SystemRepository(PostgresDataRepository repository)
    {
        _repository = repository;
    }

    public async Task<SystemResetResult> ResetSystemAsync(bool includeAdministrators)
    {
        Log.Information("ResetSystemAsync: Starting factory reset (includeAdministrators={IncludeAdministrators})", includeAdministrators);

        var db = _repository.Database.Database;

        // Identify the Metaverse Objects holding the built-in Administrator role. In the default mode
        // these are preserved so the operator is not locked out of the portal; in the total-wipe mode
        // they are removed along with everything else.
        var adminMvoIds = await db.SqlQueryRaw<Guid>(
            @"SELECT DISTINCT mr.""StaticMembersId"" AS ""Value""
              FROM ""MetaverseObjectRole"" mr
              INNER JOIN ""Roles"" r ON mr.""RolesId"" = r.""Id""
              WHERE r.""Name"" = {0}", Constants.BuiltInRoles.Administrator)
            .ToListAsync();
        var adminMvoCount = adminMvoIds.Count;
        var totalMvoCount = await _repository.Database.MetaverseObjects.CountAsync();

        // Capture counts up-front so the result reflects what we removed, regardless of any
        // FK cascade work performed during the wipe.
        var result = new SystemResetResult
        {
            ConnectedSystemsRemoved = await _repository.Database.ConnectedSystems.CountAsync(),
            ConnectedSystemObjectsRemoved = await _repository.Database.ConnectedSystemObjects.CountAsync(),
            MetaverseObjectsRemoved = includeAdministrators ? totalMvoCount : totalMvoCount - adminMvoCount,
            // Change history is truncated in full in both modes (the preserve-administrators path does not
            // snapshot it), so these are whole-table counts rather than admin-adjusted ones.
            MetaverseObjectChangesRemoved = await _repository.Database.MetaverseObjectChanges.CountAsync(),
            ConnectedSystemObjectChangesRemoved = await _repository.Database.ConnectedSystemObjectChanges.CountAsync(),
            SyncRulesRemoved = await _repository.Database.SyncRules.CountAsync(),
            ObjectMatchingRulesRemoved = await _repository.Database.ObjectMatchingRules.CountAsync(),
            SchedulesRemoved = await _repository.Database.Schedules.CountAsync(),
            ScheduleExecutionsRemoved = await _repository.Database.ScheduleExecutions.CountAsync(),
            ActivitiesRemoved = await _repository.Database.Activities.CountAsync(),
            PendingExportsRemoved = await _repository.Database.PendingExports.CountAsync(),
            CustomMetaverseObjectTypesRemoved = await _repository.Database.MetaverseObjectTypes.CountAsync(t => !t.BuiltIn),
            CustomMetaverseAttributesRemoved = await _repository.Database.MetaverseAttributes.CountAsync(a => !a.BuiltIn),
            CustomRolesRemoved = await _repository.Database.Roles.CountAsync(r => !r.BuiltIn),
            CustomConnectorDefinitionsRemoved = await _repository.Database.ConnectorDefinitions.CountAsync(c => !c.BuiltIn),
            CustomPredefinedSearchesRemoved = await _repository.Database.PredefinedSearches.CountAsync(s => !s.BuiltIn),
            CustomExampleDataSetsRemoved = await _repository.Database.ExampleDataSets.CountAsync(s => !s.BuiltIn),
            CustomExampleDataTemplatesRemoved = await _repository.Database.ExampleDataTemplates.CountAsync(t => !t.BuiltIn),
            CustomApiKeysRemoved = await _repository.Database.ApiKeys.CountAsync(k => !k.IsInfrastructureKey),
            TrustedCertificatesRemoved = await _repository.Database.TrustedCertificates.CountAsync(),
            AdministratorsRetained = includeAdministrators ? 0 : adminMvoCount,
            AdministratorsRemoved = includeAdministrators ? adminMvoCount : 0
        };

        // Clear the change tracker before issuing raw SQL — otherwise any entities materialised
        // by the count queries above (or by earlier calls in the same DbContext lifetime) will
        // still be tracked, and subsequent SaveChangesAsync calls during re-seeding could
        // resurrect them.
        _repository.Database.ChangeTracker.Clear();

        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            if (includeAdministrators)
                await WipeEverythingAsync(db);
            else
                await WipePreservingAdministratorsAsync(db, adminMvoIds);

            // Mixed tables: keep the rows seeded by SeedingServer (BuiltIn = true) and the
            // infrastructure API keys provisioned by JIM.Web/Program.cs. These deletes run in
            // both modes and cascade-clean any surviving attribute values / role memberships that
            // reference the custom rows being removed (for example a preserved administrator's
            // attribute value that pointed at a now-deleted custom Metaverse Attribute).
            await db.ExecuteSqlRawAsync(@"DELETE FROM ""PredefinedSearches"" WHERE ""BuiltIn"" = false;");
            await db.ExecuteSqlRawAsync(@"DELETE FROM ""ExampleDataTemplates"" WHERE ""BuiltIn"" = false;");
            await db.ExecuteSqlRawAsync(@"DELETE FROM ""ExampleDataSets"" WHERE ""BuiltIn"" = false;");
            await db.ExecuteSqlRawAsync(@"DELETE FROM ""Roles"" WHERE ""BuiltIn"" = false;");
            await db.ExecuteSqlRawAsync(@"DELETE FROM ""MetaverseObjectTypes"" WHERE ""BuiltIn"" = false;");
            await db.ExecuteSqlRawAsync(@"DELETE FROM ""MetaverseAttributes"" WHERE ""BuiltIn"" = false;");
            await db.ExecuteSqlRawAsync(@"DELETE FROM ""ConnectorDefinitions"" WHERE ""BuiltIn"" = false;");
            await db.ExecuteSqlRawAsync(@"DELETE FROM ""ApiKeys"" WHERE ""IsInfrastructureKey"" = false;");

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        // Drop everything from the EF change tracker post-wipe. Any entities materialised
        // earlier in the DbContext's lifetime now refer to rows that no longer exist; a
        // subsequent SaveChanges that traverses them would either resurrect deleted rows
        // (Added state) or throw a concurrency exception (Modified/Deleted state).
        _repository.Database.ChangeTracker.Clear();

        Log.Information(
            "ResetSystemAsync: Completed. Removed {ConnectedSystems} Connected Systems, {Csos} CSOs, {Mvos} MVOs ({AdminsRetained} administrators retained, {AdminsRemoved} removed), {MvoChanges} MVO change records, {CsoChanges} CSO change records, {SyncRules} Sync Rules, {MatchingRules} Object Matching Rules, {Schedules} schedules, {ScheduleExecutions} schedule executions, {Activities} activities, {PendingExports} Pending Exports, {CustomTypes} custom MV object types, {CustomAttrs} custom MV attributes, {CustomRoles} custom roles, {CustomConnectors} custom connector definitions, {CustomSearches} custom predefined searches, {CustomDataSets} custom example data sets, {CustomDataTemplates} custom example data templates, {CustomApiKeys} custom API keys, {Certs} trusted certificates",
            result.ConnectedSystemsRemoved,
            result.ConnectedSystemObjectsRemoved,
            result.MetaverseObjectsRemoved,
            result.AdministratorsRetained,
            result.AdministratorsRemoved,
            result.MetaverseObjectChangesRemoved,
            result.ConnectedSystemObjectChangesRemoved,
            result.SyncRulesRemoved,
            result.ObjectMatchingRulesRemoved,
            result.SchedulesRemoved,
            result.ScheduleExecutionsRemoved,
            result.ActivitiesRemoved,
            result.PendingExportsRemoved,
            result.CustomMetaverseObjectTypesRemoved,
            result.CustomMetaverseAttributesRemoved,
            result.CustomRolesRemoved,
            result.CustomConnectorDefinitionsRemoved,
            result.CustomPredefinedSearchesRemoved,
            result.CustomExampleDataSetsRemoved,
            result.CustomExampleDataTemplatesRemoved,
            result.CustomApiKeysRemoved,
            result.TrustedCertificatesRemoved);

        return result;
    }

    // Pure-customer tables: TRUNCATE ... RESTART IDENTITY CASCADE wipes the table, resets identity
    // sequences to 1, and follows ON DELETE CASCADE FKs to children. Order in the list doesn't matter
    // for TRUNCATE — Postgres handles dependency resolution as long as every table in the dependency
    // chain is listed (or its referencing FKs are CASCADE).
    private const string TruncateAllCustomerDataSql = @"
        TRUNCATE TABLE
            ""WorkerTasks"",
            ""DeferredReferences"",
            ""PendingExports"",
            ""ConnectedSystemObjectChanges"",
            ""MetaverseObjectChanges"",
            ""ScheduleExecutions"",
            ""Activities"",
            ""Schedules"",
            ""SyncRules"",
            ""ObjectMatchingRules"",
            ""ConnectedSystemObjects"",
            ""ConnectedSystems"",
            ""MetaverseObjects"",
            ""TrustedCertificates""
        RESTART IDENTITY CASCADE;";

    /// <summary>
    /// Total-wipe path: TRUNCATE the pure-customer tables, removing every Metaverse Object
    /// (administrators included) and resetting identity sequences. The fastest option; used when the
    /// caller has explicitly opted to remove administrators.
    /// </summary>
    private static async Task WipeEverythingAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade db)
    {
        await db.ExecuteSqlRawAsync(TruncateAllCustomerDataSql);
    }

    /// <summary>
    /// Default path: remove all customer data but preserve the Metaverse Objects holding the built-in
    /// Administrator role (and their built-in attribute values and Administrator role memberships), so
    /// the operator is not locked out of the portal.
    /// </summary>
    /// <remarks>
    /// A row-level approach cannot use the fast <c>TRUNCATE ... CASCADE</c> directly, because the cascade
    /// from the connected-system and Metaverse Object tables would truncate the entire
    /// <c>MetaverseObjectAttributeValues</c> table (it has foreign keys into both), destroying the
    /// preserved administrators' attribute values. So this path snapshots the administrators' rows into
    /// transaction-scoped temporary tables, runs the same fast full TRUNCATE, then restores them. On
    /// restore, references to now-deleted objects (other Metaverse Objects, Connected Systems, connector
    /// space objects) are nulled. Administrator attribute values that referenced custom Metaverse
    /// Attributes are subsequently cascade-removed when those custom attributes are deleted, so a
    /// preserved administrator ends up with built-in attribute values only.
    /// </remarks>
    private static async Task WipePreservingAdministratorsAsync(
        Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade db,
        List<Guid> adminMvoIds)
    {
        var adminIdParam = () => new NpgsqlParameter("adminIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = adminMvoIds.ToArray()
        };

        // 1. Snapshot the administrators' objects, attribute values, and role memberships into
        //    transaction-scoped temporary tables (dropped automatically on commit).
        await db.ExecuteSqlRawAsync(
            @"CREATE TEMP TABLE _reset_admin_mvo ON COMMIT DROP AS
              SELECT * FROM ""MetaverseObjects"" WHERE ""Id"" = ANY(@adminIds);", adminIdParam());
        await db.ExecuteSqlRawAsync(
            @"CREATE TEMP TABLE _reset_admin_mvav ON COMMIT DROP AS
              SELECT * FROM ""MetaverseObjectAttributeValues"" WHERE ""MetaverseObjectId"" = ANY(@adminIds);", adminIdParam());
        await db.ExecuteSqlRawAsync(
            @"CREATE TEMP TABLE _reset_admin_role ON COMMIT DROP AS
              SELECT * FROM ""MetaverseObjectRole"" WHERE ""StaticMembersId"" = ANY(@adminIds);", adminIdParam());

        // 2. Full fast wipe (identical to the total-wipe path).
        await db.ExecuteSqlRawAsync(TruncateAllCustomerDataSql);

        // 3. Restore the administrators. Null out references to objects that no longer exist before
        //    re-inserting the attribute values, so the inserts do not violate foreign keys.
        await db.ExecuteSqlRawAsync(@"INSERT INTO ""MetaverseObjects"" SELECT * FROM _reset_admin_mvo;");
        await db.ExecuteSqlRawAsync(
            @"UPDATE _reset_admin_mvav SET ""ReferenceValueId"" = NULL
              WHERE ""ReferenceValueId"" IS NOT NULL AND ""ReferenceValueId"" <> ALL(@adminIds);", adminIdParam());
        await db.ExecuteSqlRawAsync(@"UPDATE _reset_admin_mvav SET ""UnresolvedReferenceValueId"" = NULL WHERE ""UnresolvedReferenceValueId"" IS NOT NULL;");
        await db.ExecuteSqlRawAsync(@"UPDATE _reset_admin_mvav SET ""ContributedBySystemId"" = NULL WHERE ""ContributedBySystemId"" IS NOT NULL;");
        await db.ExecuteSqlRawAsync(@"INSERT INTO ""MetaverseObjectAttributeValues"" SELECT * FROM _reset_admin_mvav;");
        await db.ExecuteSqlRawAsync(@"INSERT INTO ""MetaverseObjectRole"" SELECT * FROM _reset_admin_role;");
    }
}
