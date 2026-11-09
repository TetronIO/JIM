// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.DirectoryServices.Protocols;
using JIM.Application.Servers;
using JIM.Application.Staging;
using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Tests.Connectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Npgsql;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Measures what the export's deferred pass pays, step by step, to write one large group being created: a Create whose
/// member changes all wait on the deferred pass to resolve them, as an initial load stages a group of
/// <see cref="MemberCount"/> members (Scale500k25kGroups, Scenario 8). Walks the parallel deferred path's database steps
/// in order (load the chunk, fetch the anchors, resolve, persist the resolutions, re-load the batch on a fresh context,
/// write the Pending Export after success, optimistic apply) and the work JIM and the LDAP Connector do inside the
/// <c>ExportDeferredBatch</c> span with the directory stubbed out, so what is left of that span is the directory's.
/// Beside the current Pending Export write it times a set-based status update of the same rows, to size the cost of
/// writing every change row back with its values. Explicit, because it seeds about 600,000 rows and exists to be read,
/// not to guard; the numbers go to the test output.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
[Explicit("Benchmark: seeds a group of 200,000 members being created; run on demand")]
public class DeferredGroupCreateExportBenchmarkDatabaseTests
{
    private const int MemberCount = 200_000;
    private const int ModifyBatchSize = LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the deferred group Create benchmark.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass};Command Timeout=600";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    [Test]
    public async Task DeferredPass_GroupCreateWithLargeMembership_ReportsEachStepAsync()
    {
        var seeded = await SeedAsync();
        var timings = new List<(string Step, double Ms, string Detail)>();
        var stopwatch = Stopwatch.StartNew();

        // LoadDeferredChunk: the deferred pass loads the chunk on the caller's context.
        List<PendingExport> chunk;
        await using (var ctx = NewContext())
        {
            stopwatch.Restart();
            chunk = await new PostgresDataRepository(ctx).Sync.GetPendingExportsByIdsAsync([seeded.ExportId]);
            timings.Add(("LoadDeferredChunk", stopwatch.Elapsed.TotalMilliseconds, $"{chunk.Sum(pe => pe.AttributeValueChanges.Count):N0} changes"));

            // FetchReferenceAnchors
            var mvoIds = chunk.SelectMany(pe => pe.AttributeValueChanges)
                .Select(c => Guid.TryParse(c.UnresolvedReferenceValue, out var id) ? id : (Guid?)null)
                .OfType<Guid>().ToHashSet();
            Dictionary<Guid, ReferenceAnchorSummary> anchors = [];
            foreach (var pass in new[] { "first", "again" })
            {
                stopwatch.Restart();
                anchors = await new PostgresDataRepository(ctx).Sync.GetReferenceAnchorsByMetaverseObjectIdsAsync(mvoIds, seeded.SystemId);
                timings.Add(($"FetchReferenceAnchors ({pass})", stopwatch.Elapsed.TotalMilliseconds, $"{anchors.Count:N0} anchors"));
            }

            // Resolution, in memory
            var resolvedChanges = new List<PendingExportAttributeValueChange>();
            stopwatch.Restart();
            var allResolved = chunk.All(pe => ExportExecutionServer.TryResolveReferencesFromLookup(pe, anchors, resolvedChanges));
            timings.Add(("TryResolveReferencesFromLookup", stopwatch.Elapsed.TotalMilliseconds, $"{resolvedChanges.Count:N0} resolved"));
            Assert.That(allResolved, Is.True, "every member has an anchor");
            foreach (var pe in chunk)
                pe.HasUnresolvedReferences = false;

            // PersistResolvedReferences
            stopwatch.Restart();
            await new PostgresDataRepository(ctx).Sync.PersistResolvedReferencesAsync(chunk, resolvedChanges);
            timings.Add(("PersistResolvedReferences", stopwatch.Elapsed.TotalMilliseconds, $"{resolvedChanges.Count:N0} rows updated"));
        }

        // ReloadExportBatch: the parallel worker re-loads the batch on its own context.
        await using (var batchContext = NewContext())
        {
            var batchRepo = new PostgresDataRepository(batchContext).Sync;
            stopwatch.Restart();
            var batch = await batchRepo.GetPendingExportsByIdsAsync([seeded.ExportId]);
            timings.Add(("ReloadExportBatch", stopwatch.Elapsed.TotalMilliseconds, $"{batch.Sum(pe => pe.AttributeValueChanges.Count):N0} changes"));

            // Inside the ExportDeferredBatch span, JIM's side: the two validators and ForConnector.
            stopwatch.Restart();
            foreach (var pe in batch)
            {
                Assert.That(SingleValuedAttributeChangeValidator.Check(ExportExecutionServer.ForConnector(pe)), Is.Null);
                Assert.That(ClassMembershipValidator.Check(pe, seeded.System), Is.Null);
            }
            var forConnector = batch.Select(ExportExecutionServer.ForConnector).ToList();
            timings.Add(("ExportDeferredBatch: validators + ForConnector", stopwatch.Elapsed.TotalMilliseconds, ""));

            // Inside the ExportDeferredBatch span, the LDAP Connector with the directory stubbed: every request answered
            // at once, so this is the Connector's own CPU and the request count the directory has to serve.
            var requestCounts = new Dictionary<string, int>();
            var executor = new Mock<ILdapOperationExecutor>();
            executor.Setup(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>())).ReturnsAsync((DirectoryRequest request) =>
            {
                lock (requestCounts)
                    requestCounts[request.GetType().Name] = requestCounts.GetValueOrDefault(request.GetType().Name) + 1;
                return request switch
                {
                    AddRequest => LdapTestResponses.Create<AddResponse>(ResultCode.Success),
                    ModifyRequest => LdapTestResponses.Create<ModifyResponse>(ResultCode.Success),
                    _ => LdapTestResponses.Create<SearchResponse>(ResultCode.Success)
                };
            });
            var connector = new LdapConnectorExport(executor.Object, [], Log.Logger, exportConcurrency: 4,
                modifyBatchSize: ModifyBatchSize, directoryType: LdapDirectoryType.OpenLDAP);
            stopwatch.Restart();
            var results = await connector.ExecuteAsync(forConnector, CancellationToken.None);
            timings.Add(("ExportDeferredBatch: LDAP Connector, directory stubbed", stopwatch.Elapsed.TotalMilliseconds,
                string.Join(", ", requestCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Value:N0} {kv.Key}"))));
            Assert.That(results.All(r => r.Success), Is.True, string.Join("; ", results.Select(r => r.ErrorMessage)));

            // ProcessBatchSuccess, as it marks the written changes and the export (UpdateAttributeChangeStatusesAfterExport).
            var now = DateTime.UtcNow;
            foreach (var pe in batch)
            {
                pe.Status = PendingExportStatus.Exported;
                foreach (var change in pe.AttributeValueChanges.Where(c => c.Status == PendingExportAttributeChangeStatus.Pending))
                {
                    change.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
                    change.ExportAttemptCount++;
                    change.LastExportedAt = now;
                }
            }

            stopwatch.Restart();
            await using (var transaction = await batchContext.Database.BeginTransactionAsync())
            {
                await batchRepo.UpdatePendingExportsAsync(batch);
                await transaction.CommitAsync();
            }
            timings.Add(("UpdatePendingExports (every change row written back)", stopwatch.Elapsed.TotalMilliseconds,
                $"{batch.Sum(pe => pe.AttributeValueChanges.Count):N0} rows"));

            // The same marking as one set-based statement per export, for comparison.
            stopwatch.Restart();
            await using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    @"UPDATE ""PendingExportAttributeValueChanges""
                      SET ""Status"" = @exported, ""ExportAttemptCount"" = ""ExportAttemptCount"" + 1, ""LastExportedAt"" = @now
                      WHERE ""PendingExportId"" = ANY(@ids) AND ""Status"" = ANY(@sendable)", connection);
                command.Parameters.AddWithValue("exported", (int)PendingExportAttributeChangeStatus.ExportedPendingConfirmation);
                command.Parameters.AddWithValue("now", now);
                command.Parameters.AddWithValue("ids", batch.Select(pe => pe.Id).ToArray());
                // Matches the rows above (already ExportedPendingConfirmation), so it rewrites as many rows as they did.
                command.Parameters.AddWithValue("sendable", new[] { (int)PendingExportAttributeChangeStatus.ExportedPendingConfirmation });
                var rows = await command.ExecuteNonQueryAsync();
                timings.Add(("  for comparison: set-based status UPDATE of the same rows", stopwatch.Elapsed.TotalMilliseconds, $"{rows:N0} rows"));
            }

            // OptimisticApply: Calculate and Persist.
            stopwatch.Restart();
            var delta = OptimisticExportApplyCalculator.CalculateDelta(batch);
            timings.Add(("OptimisticApply.Calculate", stopwatch.Elapsed.TotalMilliseconds, $"{delta.Additions.Count:N0} additions"));
            var affected = delta.Additions.Select(a => a.ConnectedSystemObject.Id).ToHashSet();
            stopwatch.Restart();
            await batchRepo.ApplyExportedAttributeValuesAsync(delta.Additions, delta.RemovalValueIds, affected);
            timings.Add(("OptimisticApply.Persist", stopwatch.Elapsed.TotalMilliseconds, $"{delta.Additions.Count:N0} rows copied"));
        }

        TestContext.Out.WriteLine($"Group Create of {MemberCount:N0} members through the deferred pass (Modify Batch Size {ModifyBatchSize:N0}):");
        foreach (var (step, ms, detail) in timings)
            TestContext.Out.WriteLine($"  {step,-58} {ms,10:N1} ms  {detail}");
    }

    private sealed record Seeded(ConnectedSystem System, int SystemId, Guid GroupId, Guid ExportId);

    /// <summary>
    /// Seeds <see cref="MemberCount"/> users, each joined to a Metaverse Object and holding its DN, and a group
    /// provisioned but not yet written: its Connected System Object awaiting provisioning with no values, and a Create
    /// carrying its DN, cn and objectClass, and one member Add per user naming the user's Metaverse Object, unresolved,
    /// as export staging leaves a member it defers.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        ConnectedSystem system;
        int groupTypeId, userTypeId, dnAttributeId, cnAttributeId, objectClassAttributeId, memberAttributeId, userDnAttributeId, mvoTypeId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
            var groupType = new ConnectedSystemObjectType { Name = "groupOfNames", ConnectedSystem = system, Selected = true };
            var userType = new ConnectedSystemObjectType { Name = "inetOrgPerson", ConnectedSystem = system, Selected = true };
            ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType type, string name, AttributeDataType dataType,
                AttributePlurality plurality, bool secondaryExternalId = false)
            {
                var attribute = new ConnectedSystemObjectTypeAttribute
                {
                    Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = plurality, Selected = true,
                    IsSecondaryExternalId = secondaryExternalId
                };
                type.Attributes.Add(attribute);
                return attribute;
            }
            var dn = Attribute(groupType, "distinguishedName", AttributeDataType.Text, AttributePlurality.SingleValued, secondaryExternalId: true);
            var cn = Attribute(groupType, "cn", AttributeDataType.Text, AttributePlurality.SingleValued);
            var objectClass = Attribute(groupType, "objectClass", AttributeDataType.Text, AttributePlurality.MultiValued);
            var member = Attribute(groupType, "member", AttributeDataType.Reference, AttributePlurality.MultiValued);
            var userDn = Attribute(userType, "distinguishedName", AttributeDataType.Text, AttributePlurality.SingleValued, secondaryExternalId: true);
            var mvoType = new MetaverseObjectType { Name = "User", PluralName = "Users" };
            seed.AddRange(connectorDefinition, system, groupType, userType, mvoType);
            await seed.SaveChangesAsync();
            (groupTypeId, userTypeId, dnAttributeId, cnAttributeId, objectClassAttributeId, memberAttributeId, userDnAttributeId, mvoTypeId) =
                (groupType.Id, userType.Id, dn.Id, cn.Id, objectClass.Id, member.Id, userDn.Id, mvoType.Id);
        }

        var groupId = Guid.NewGuid();
        var exportId = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                @"CREATE TEMP TABLE users AS
                    SELECT g AS n, gen_random_uuid() AS id, gen_random_uuid() AS mvo, 'cn=user' || g || ',ou=people,dc=glitterband,dc=local' AS dn
                    FROM generate_series(1, @members) g;
                  INSERT INTO ""MetaverseObjects"" (""Id"", ""Created"", ""TypeId"", ""Status"", ""DeletionInitiatedByType"", ""Origin"")
                    SELECT mvo, now(), @mvoType, 0, 0, 0 FROM users;
                  INSERT INTO ""ConnectedSystemObjects"" (""Id"", ""Created"", ""TypeId"", ""ConnectedSystemId"", ""ExternalIdAttributeId"",
                      ""SecondaryExternalIdAttributeId"", ""Status"", ""JoinType"", ""MetaverseObjectId"")
                    SELECT id, now(), @userType, @system, @userDn, @userDn, 0, 0, mvo FROM users;
                  INSERT INTO ""ConnectedSystemObjects"" (""Id"", ""Created"", ""TypeId"", ""ConnectedSystemId"", ""ExternalIdAttributeId"",
                      ""SecondaryExternalIdAttributeId"", ""Status"", ""JoinType"")
                    VALUES (@group, now(), @groupType, @system, @dn, @dn, @pendingProvisioning, 0);
                  INSERT INTO ""ConnectedSystemObjectAttributeValues"" (""Id"", ""AttributeId"", ""ConnectedSystemObjectId"", ""StringValue"")
                    SELECT gen_random_uuid(), @userDn, id, dn FROM users;
                  INSERT INTO ""PendingExports"" (""Id"", ""ConnectedSystemId"", ""ConnectedSystemObjectId"", ""ChangeType"", ""Status"", ""CreatedAt"",
                      ""ErrorCount"", ""HasUnresolvedReferences"")
                    VALUES (@export, @system, @group, @create, @pending, now(), 0, true);
                  INSERT INTO ""PendingExportAttributeValueChanges"" (""Id"", ""PendingExportId"", ""AttributeId"", ""ChangeType"", ""Status"",
                      ""ExportAttemptCount"", ""StringValue"")
                    VALUES (gen_random_uuid(), @export, @dn, @update, @pendingChange, 0, 'cn=All Staff,ou=groups,dc=glitterband,dc=local'),
                           (gen_random_uuid(), @export, @cn, @update, @pendingChange, 0, 'All Staff'),
                           (gen_random_uuid(), @export, @objectClass, @add, @pendingChange, 0, 'groupOfNames');
                  INSERT INTO ""PendingExportAttributeValueChanges"" (""Id"", ""PendingExportId"", ""AttributeId"", ""ChangeType"", ""Status"",
                      ""ExportAttemptCount"", ""UnresolvedReferenceValue"")
                    SELECT gen_random_uuid(), @export, @member, @add, @pendingChange, 0, mvo::text FROM users;
                  ANALYZE;", connection);
            command.CommandTimeout = 600;
            command.Parameters.AddWithValue("members", MemberCount);
            command.Parameters.AddWithValue("mvoType", mvoTypeId);
            command.Parameters.AddWithValue("userType", userTypeId);
            command.Parameters.AddWithValue("groupType", groupTypeId);
            command.Parameters.AddWithValue("system", system.Id);
            command.Parameters.AddWithValue("userDn", userDnAttributeId);
            command.Parameters.AddWithValue("dn", dnAttributeId);
            command.Parameters.AddWithValue("cn", cnAttributeId);
            command.Parameters.AddWithValue("objectClass", objectClassAttributeId);
            command.Parameters.AddWithValue("member", memberAttributeId);
            command.Parameters.AddWithValue("group", groupId);
            command.Parameters.AddWithValue("export", exportId);
            command.Parameters.AddWithValue("pendingProvisioning", (int)ConnectedSystemObjectStatus.PendingProvisioning);
            command.Parameters.AddWithValue("create", (int)PendingExportChangeType.Create);
            command.Parameters.AddWithValue("pending", (int)PendingExportStatus.Pending);
            command.Parameters.AddWithValue("add", (int)PendingExportAttributeChangeType.Add);
            command.Parameters.AddWithValue("update", (int)PendingExportAttributeChangeType.Update);
            command.Parameters.AddWithValue("pendingChange", (int)PendingExportAttributeChangeStatus.Pending);
            await command.ExecuteNonQueryAsync();
        }

        return new Seeded(system, system.Id, groupId, exportId);
    }
}
