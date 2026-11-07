// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Measures what export execution pays to load and apply one large group's Pending Export (#2816): a group holding
/// 200,000 resolved members and an Update removing 1,000 of them, as a leaver step stages it. Explicit, because it seeds
/// about 400,000 rows and exists to be read, not to guard; the numbers go to the test output.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
[Explicit("Benchmark (#2816): seeds 200,000 members; run on demand")]
public class ExportBatchLoadScaleBenchmarkDatabaseTests
{
    private const int MemberCount = 200_000;
    private const int RemovalCount = 1_000;
    private const int Runs = 3;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the export batch load benchmark.");

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
    public async Task GetPendingExportsByIdsAsync_LargeGroupLosingAThousandMembers_ReportsLoadAndCalculateTimesAsync()
    {
        var seeded = await SeedAsync();

        var loadTimes = new List<long>();
        var calculateTimes = new List<long>();
        var leanTimes = new List<long>();
        var leanCalculateTimes = new List<double>();
        int loadedValues = 0, removals = 0, leanRows = 0;
        for (var run = 0; run < Runs; run++)
        {
            await using var ctx = NewContext();
            var repository = new PostgresDataRepository(ctx);

            var stopwatch = Stopwatch.StartNew();
            var batch = await repository.Sync.GetPendingExportsByIdsAsync([seeded.ExportId]);
            loadTimes.Add(stopwatch.ElapsedMilliseconds);
            loadedValues = batch.Sum(pe => pe.ConnectedSystemObject!.AttributeValues.Count);

            stopwatch.Restart();
            var delta = OptimisticExportApplyCalculator.CalculateDelta(batch);
            calculateTimes.Add(stopwatch.ElapsedMilliseconds);
            removals = delta.RemovalValueIds.Count;

            // The same calculation over the object as a lean load would hand it over: its values outside the member
            // attribute and the member rows the changes name.
            var named = seeded.RemovedUserIds.ToHashSet();
            var full = batch.Single();
            var leanObject = new ConnectedSystemObject
            {
                Id = full.ConnectedSystemObject!.Id,
                AttributeValues = new(full.ConnectedSystemObject.AttributeValues
                    .Where(av => av.AttributeId != seeded.MemberAttributeId || (av.ReferenceValueId is { } id && named.Contains(id))))
            };
            var leanExport = new PendingExport
            {
                Id = full.Id, ChangeType = full.ChangeType, ConnectedSystemObject = leanObject,
                AttributeValueChanges = full.AttributeValueChanges
            };
            stopwatch.Restart();
            var leanDelta = OptimisticExportApplyCalculator.CalculateDelta([leanExport]);
            leanCalculateTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
            Assert.That(leanDelta.RemovalValueIds, Is.EquivalentTo(delta.RemovalValueIds), "the lean object yields the same removals");

            // What a lean load would read instead: the object's values other than the member attribute, the member rows the
            // changes name, and the member count.
            stopwatch.Restart();
            leanRows = await ProjectLeanLoadAsync(seeded);
            leanTimes.Add(stopwatch.ElapsedMilliseconds);
        }

        TestContext.Out.WriteLine($"Group of {MemberCount:N0} members, Update removing {RemovalCount:N0} ({Runs} runs, first run cold):");
        TestContext.Out.WriteLine($"  GetPendingExportsByIdsAsync: {string.Join(", ", loadTimes)} ms; {loadedValues:N0} values loaded");
        TestContext.Out.WriteLine($"  OptimisticExportApplyCalculator.CalculateDelta: {string.Join(", ", calculateTimes)} ms; {removals:N0} removals");
        TestContext.Out.WriteLine($"  CalculateDelta over the lean object: {string.Join(", ", leanCalculateTimes.Select(t => t.ToString("0.0")))} ms");
        TestContext.Out.WriteLine($"  Lean load projection (raw SQL): {string.Join(", ", leanTimes)} ms; {leanRows:N0} rows");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loadedValues, Is.EqualTo(MemberCount + 1), "the full load reads every member and the group's DN");
            Assert.That(removals, Is.EqualTo(RemovalCount), "every removed member is held, so each is removed");
        }
    }

    private sealed record Seeded(Guid GroupId, Guid ExportId, int MemberAttributeId, Guid[] RemovedUserIds);

    /// <summary>
    /// The three statements a lean load would run for the seeded export, timed together: every value of the group outside
    /// the member attribute, the member rows naming the removed users, and the group's member count.
    /// </summary>
    private async Task<int> ProjectLeanLoadAsync(Seeded seeded)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var rows = 0;

        await using (var command = new NpgsqlCommand(
            @"SELECT * FROM ""ConnectedSystemObjectAttributeValues"" WHERE ""ConnectedSystemObjectId"" = @id AND ""AttributeId"" <> @attr", connection))
        {
            command.Parameters.AddWithValue("id", seeded.GroupId);
            command.Parameters.AddWithValue("attr", seeded.MemberAttributeId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows++;
        }

        await using (var command = new NpgsqlCommand(
            @"SELECT * FROM ""ConnectedSystemObjectAttributeValues""
              WHERE ""ReferenceValueId"" = ANY(@refs) AND ""ConnectedSystemObjectId"" = @id AND ""AttributeId"" = @attr", connection))
        {
            command.Parameters.AddWithValue("refs", seeded.RemovedUserIds);
            command.Parameters.AddWithValue("id", seeded.GroupId);
            command.Parameters.AddWithValue("attr", seeded.MemberAttributeId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows++;
        }

        await using (var command = new NpgsqlCommand(
            @"SELECT count(*) FROM ""ConnectedSystemObjectAttributeValues"" WHERE ""ConnectedSystemObjectId"" = @id AND ""AttributeId"" = @attr", connection))
        {
            command.Parameters.AddWithValue("id", seeded.GroupId);
            command.Parameters.AddWithValue("attr", seeded.MemberAttributeId);
            rows += (await command.ExecuteScalarAsync()) is long ? 1 : 0;
        }

        return rows;
    }

    /// <summary>
    /// Seeds a groupOfNames whose members are user objects, each held as a resolved reference that keeps the user's DN
    /// (as an import records it), and an Update removing the first <see cref="RemovalCount"/> of them, each change naming
    /// its user by id and DN (as a leaver's reference recall stages it).
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        int systemId, groupTypeId, userTypeId, dnAttributeId, memberAttributeId, userDnAttributeId;
        await using (var seed = NewContext())
        {
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
            seed.AddRange(connectorDefinition, system, groupType, userType);
            await seed.SaveChangesAsync();
            (systemId, groupTypeId, userTypeId, dnAttributeId, memberAttributeId, userDnAttributeId) =
                (system.Id, groupType.Id, userType.Id, dn.Id, member.Id, userDn.Id);
        }

        var groupId = Guid.NewGuid();
        var exportId = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                @"CREATE TEMP TABLE users AS
                    SELECT g AS n, gen_random_uuid() AS id, 'cn=user' || g || ',ou=people,dc=glitterband,dc=local' AS dn
                    FROM generate_series(1, @members) g;
                  INSERT INTO ""ConnectedSystemObjects"" (""Id"", ""Created"", ""TypeId"", ""ConnectedSystemId"", ""ExternalIdAttributeId"",
                      ""SecondaryExternalIdAttributeId"", ""Status"", ""JoinType"")
                    SELECT id, now(), @userType, @system, @userDn, @userDn, 0, 0 FROM users
                    UNION ALL SELECT @group, now(), @groupType, @system, @dn, @dn, 0, 0;
                  INSERT INTO ""ConnectedSystemObjectAttributeValues"" (""Id"", ""AttributeId"", ""ConnectedSystemObjectId"", ""StringValue"")
                    VALUES (gen_random_uuid(), @dn, @group, 'cn=All Staff,ou=groups,dc=glitterband,dc=local');
                  INSERT INTO ""ConnectedSystemObjectAttributeValues"" (""Id"", ""AttributeId"", ""ConnectedSystemObjectId"", ""ReferenceValueId"", ""UnresolvedReferenceValue"")
                    SELECT gen_random_uuid(), @member, @group, id, dn FROM users;
                  INSERT INTO ""PendingExports"" (""Id"", ""ConnectedSystemId"", ""ConnectedSystemObjectId"", ""ChangeType"", ""Status"", ""CreatedAt"",
                      ""ErrorCount"", ""HasUnresolvedReferences"")
                    VALUES (@export, @system, @group, @update, @pending, now(), 0, false);
                  INSERT INTO ""PendingExportAttributeValueChanges"" (""Id"", ""PendingExportId"", ""AttributeId"", ""ChangeType"", ""Status"",
                      ""ExportAttemptCount"", ""StringValue"", ""ResolvedReferenceCsoId"")
                    SELECT gen_random_uuid(), @export, @member, @remove, @pendingChange, 0, dn, id FROM users WHERE n <= @removals;
                  ANALYZE ""ConnectedSystemObjectAttributeValues"";", connection);
            command.CommandTimeout = 600;
            command.Parameters.AddWithValue("members", MemberCount);
            command.Parameters.AddWithValue("removals", RemovalCount);
            command.Parameters.AddWithValue("userType", userTypeId);
            command.Parameters.AddWithValue("groupType", groupTypeId);
            command.Parameters.AddWithValue("system", systemId);
            command.Parameters.AddWithValue("userDn", userDnAttributeId);
            command.Parameters.AddWithValue("dn", dnAttributeId);
            command.Parameters.AddWithValue("member", memberAttributeId);
            command.Parameters.AddWithValue("group", groupId);
            command.Parameters.AddWithValue("export", exportId);
            command.Parameters.AddWithValue("update", (int)PendingExportChangeType.Update);
            command.Parameters.AddWithValue("pending", (int)PendingExportStatus.Pending);
            command.Parameters.AddWithValue("remove", (int)PendingExportAttributeChangeType.Remove);
            command.Parameters.AddWithValue("pendingChange", (int)PendingExportAttributeChangeStatus.Pending);
            await command.ExecuteNonQueryAsync();
        }

        var removedUserIds = new List<Guid>();
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                @"SELECT ""ResolvedReferenceCsoId"" FROM ""PendingExportAttributeValueChanges"" WHERE ""PendingExportId"" = @export", connection);
            command.Parameters.AddWithValue("export", exportId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                removedUserIds.Add(reader.GetGuid(0));
        }

        return new Seeded(groupId, exportId, memberAttributeId, removedUserIds.ToArray());
    }
}
