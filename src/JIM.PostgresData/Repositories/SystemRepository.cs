// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
using Serilog;
namespace JIM.PostgresData.Repositories;

internal sealed class SystemRepository : ISystemRepository
{
    private readonly PostgresDataRepository _repository;

    public SystemRepository(PostgresDataRepository repository)
    {
        _repository = repository;
    }

    public async Task<SystemResetResult> ResetSystemAsync()
    {
        Log.Information("ResetSystemAsync: Starting factory reset");

        var db = _repository.Database.Database;

        // Capture counts up-front so the result reflects what we removed, regardless of any
        // FK cascade work performed during the wipe.
        var result = new SystemResetResult
        {
            ConnectedSystemsRemoved = await _repository.Database.ConnectedSystems.CountAsync(),
            ConnectedSystemObjectsRemoved = await _repository.Database.ConnectedSystemObjects.CountAsync(),
            MetaverseObjectsRemoved = await _repository.Database.MetaverseObjects.CountAsync(),
            SyncRulesRemoved = await _repository.Database.SyncRules.CountAsync(),
            SchedulesRemoved = await _repository.Database.Schedules.CountAsync(),
            ActivitiesRemoved = await _repository.Database.Activities.CountAsync(),
            PendingExportsRemoved = await _repository.Database.PendingExports.CountAsync(),
            CustomMetaverseObjectTypesRemoved = await _repository.Database.MetaverseObjectTypes.CountAsync(t => !t.BuiltIn),
            CustomMetaverseAttributesRemoved = await _repository.Database.MetaverseAttributes.CountAsync(a => !a.BuiltIn),
            CustomRolesRemoved = await _repository.Database.Roles.CountAsync(r => !r.BuiltIn),
            CustomConnectorDefinitionsRemoved = await _repository.Database.ConnectorDefinitions.CountAsync(c => !c.BuiltIn),
            CustomPredefinedSearchesRemoved = await _repository.Database.PredefinedSearches.CountAsync(s => !s.BuiltIn),
            CustomExampleDataSetsRemoved = await _repository.Database.ExampleDataSets.CountAsync(s => !s.BuiltIn),
            CustomApiKeysRemoved = await _repository.Database.ApiKeys.CountAsync(k => !k.IsInfrastructureKey),
            TrustedCertificatesRemoved = await _repository.Database.TrustedCertificates.CountAsync()
        };

        // Clear the change tracker before issuing raw SQL — otherwise any entities materialised
        // by the count queries above (or by earlier calls in the same DbContext lifetime) will
        // still be tracked, and subsequent SaveChangesAsync calls during re-seeding could
        // resurrect them.
        _repository.Database.ChangeTracker.Clear();

        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            // Pure-customer tables: TRUNCATE ... RESTART IDENTITY CASCADE wipes the table,
            // resets identity sequences to 1, and follows ON DELETE CASCADE FKs to children.
            // Order in the list doesn't matter for TRUNCATE — Postgres handles dependency
            // resolution as long as every table in the dependency chain is listed (or its
            // referencing FKs are CASCADE).
            await db.ExecuteSqlRawAsync(@"
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
                RESTART IDENTITY CASCADE;");

            // Mixed tables: keep the rows seeded by SeedingServer (BuiltIn = true) and the
            // infrastructure API keys provisioned by JIM.Web/Program.cs. Identity sequences
            // for these tables intentionally retain their current value — the gap from
            // deleted custom rows is harmless and resetting would risk collisions with
            // preserved built-in IDs.
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
            "ResetSystemAsync: Completed. Removed {ConnectedSystems} connected systems, {Csos} CSOs, {Mvos} MVOs, {SyncRules} sync rules, {Schedules} schedules, {Activities} activities, {PendingExports} pending exports, {CustomTypes} custom MV object types, {CustomAttrs} custom MV attributes, {CustomRoles} custom roles, {CustomConnectors} custom connector definitions, {CustomSearches} custom predefined searches, {CustomDataSets} custom example data sets, {CustomApiKeys} custom API keys, {Certs} trusted certificates",
            result.ConnectedSystemsRemoved,
            result.ConnectedSystemObjectsRemoved,
            result.MetaverseObjectsRemoved,
            result.SyncRulesRemoved,
            result.SchedulesRemoved,
            result.ActivitiesRemoved,
            result.PendingExportsRemoved,
            result.CustomMetaverseObjectTypesRemoved,
            result.CustomMetaverseAttributesRemoved,
            result.CustomRolesRemoved,
            result.CustomConnectorDefinitionsRemoved,
            result.CustomPredefinedSearchesRemoved,
            result.CustomExampleDataSetsRemoved,
            result.CustomApiKeysRemoved,
            result.TrustedCertificatesRemoved);

        return result;
    }
}
