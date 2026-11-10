// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Measures what the indexes on <c>ConnectedSystemObjectAttributeValues</c> cost an import's COPY (#2868): about a million
/// value rows written as a Full Import writes them, in the import writer's column list and types, for users holding text
/// and Guid values and groups whose members are resolved references keeping their DN. Each shape of the reference indexes
/// is timed in turn on the same seeded objects, three times over, with the table emptied and checkpointed between runs:
/// <list type="bullet">
/// <item>before #2868: <c>IX_..._ReferenceValueId</c> over every row;</item>
/// <item>#2868: that index partial (<c>ReferenceValueId IS NOT NULL</c>), and <c>IX_..._UnresolvedReferenceValue</c> over
/// every reference's DN;</item>
/// <item>the option #2871 weighed: <c>IX_..._UnresolvedReferenceValue</c> narrowed to outstanding values;</item>
/// <item>#2871: <c>IX_..._UnresolvedReferenceValue</c> dropped, reconciliation's probe seeking outstanding values by the
/// lowered index alone.</item>
/// </list>
/// The schema is put back as migrated afterwards. Explicit, because it writes millions of rows and exists to be read,
/// not to guard; the numbers go to the test output.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
[Explicit("Benchmark: writes about a million attribute value rows twelve times; run on demand")]
public class ConnectedSystemObjectAttributeValueCopyBenchmarkDatabaseTests
{
    private const int UserCount = 100_000;
    private const int GroupCount = 400;
    private const int MembersPerGroup = 1_000;
    private const int Rounds = 3;

    private const string ReferenceIndex = "IX_ConnectedSystemObjectAttributeValues_ReferenceValueId";

    // Stored cut to PostgreSQL's 63-character identifier limit.
    private const string UnresolvedReferenceIndex = "IX_ConnectedSystemObjectAttributeValues_UnresolvedReferenceValu";

    private static readonly string[] UserTextAttributes = ["givenName", "sn", "displayName", "mail"];

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the attribute value COPY benchmark.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass};Command Timeout=900";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    [Test]
    public async Task ImportCopy_ReferenceIndexShapes_ReportsTimeAndIndexSizesAsync()
    {
        var seeded = await SeedObjectsAsync();
        var shapes = new (string Name, string Sql)[]
        {
            ("before #2868 (ReferenceValueId index whole)", $"""
                {MigratedShapeSql}
                DROP INDEX "{ReferenceIndex}";
                CREATE INDEX "{ReferenceIndex}" ON "ConnectedSystemObjectAttributeValues" ("ReferenceValueId");
                CREATE INDEX "{UnresolvedReferenceIndex}" ON "ConnectedSystemObjectAttributeValues" ("UnresolvedReferenceValue")
                    WHERE "UnresolvedReferenceValue" IS NOT NULL;
                """),
            ("#2868 (ReferenceValueId index partial)", $"""
                {MigratedShapeSql}
                CREATE INDEX "{UnresolvedReferenceIndex}" ON "ConnectedSystemObjectAttributeValues" ("UnresolvedReferenceValue")
                    WHERE "UnresolvedReferenceValue" IS NOT NULL;
                """),
            ("option: DN index narrowed to outstanding values", $"""
                {MigratedShapeSql}
                CREATE INDEX "{UnresolvedReferenceIndex}" ON "ConnectedSystemObjectAttributeValues" ("UnresolvedReferenceValue")
                    WHERE "UnresolvedReferenceValue" IS NOT NULL AND "ReferenceValueId" IS NULL;
                """),
            ("#2871 (DN index dropped)", MigratedShapeSql)
        };

        var results = new List<(string Shape, int Round, double Seconds, long WalBytes, long Rows, IReadOnlyDictionary<string, long> IndexBytes)>();
        try
        {
            for (var round = 1; round <= Rounds; round++)
            {
                foreach (var (name, sql) in shapes)
                {
                    await ExecuteAsync($"""TRUNCATE "ConnectedSystemObjectAttributeValues"; {sql}""");
                    await ExecuteAsync("CHECKPOINT");
                    var walBefore = await CurrentWalPositionAsync();
                    var (seconds, rows) = await CopyValuesAsync(seeded);
                    var walBytes = await CurrentWalPositionAsync() - walBefore;
                    results.Add((name, round, seconds, walBytes, rows, await IndexSizesAsync()));
                }
            }
        }
        finally
        {
            await ExecuteAsync(MigratedShapeSql);
        }

        TestContext.Out.WriteLine($"COPY into ConnectedSystemObjectAttributeValues: {UserCount:N0} users x {2 + UserTextAttributes.Length} values, " +
                                  $"{GroupCount:N0} groups x {MembersPerGroup:N0} resolved members (plus each group's 2 values)");
        foreach (var result in results)
        {
            TestContext.Out.WriteLine($"  round {result.Round}  {result.Shape,-52} {result.Seconds,7:F2} s  WAL {result.WalBytes / 1048576.0,6:F0} MB  " +
                                      $"ReferenceValueId {Megabytes(result.IndexBytes, ReferenceIndex)}, " +
                                      $"UnresolvedReferenceValue {Megabytes(result.IndexBytes, UnresolvedReferenceIndex)}, " +
                                      $"all indexes {result.IndexBytes.Values.Sum() / 1048576.0:F1} MB");
        }

        foreach (var shape in results.GroupBy(r => r.Shape))
            TestContext.Out.WriteLine($"  best of {Rounds}: {shape.Key,-52} {shape.Min(r => r.Seconds),7:F2} s, median {Median(shape.Select(r => r.Seconds)),7:F2} s, WAL {shape.Min(r => r.WalBytes) / 1048576.0:F0} MB");

        Assert.That(results.Select(r => r.Rows).Distinct().Single(), Is.EqualTo(UserCount * (2L + UserTextAttributes.Length) + GroupCount * (2L + MembersPerGroup)));
    }

    /// <summary>The reference indexes as the migrations leave them: the id index partial, and no index on the DN alone.</summary>
    private static readonly string MigratedShapeSql = $"""
        DROP INDEX IF EXISTS "{ReferenceIndex}";
        CREATE INDEX "{ReferenceIndex}" ON "ConnectedSystemObjectAttributeValues" ("ReferenceValueId") WHERE "ReferenceValueId" IS NOT NULL;
        DROP INDEX IF EXISTS "{UnresolvedReferenceIndex}";
        """;

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private async Task<long> CurrentWalPositionAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT pg_wal_lsn_diff(pg_current_wal_lsn(), '0/0')::bigint", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static string Megabytes(IReadOnlyDictionary<string, long> sizes, string index) =>
        $"{sizes.GetValueOrDefault(index) / 1048576.0:F1} MB";

    private static string UserDn(int n) => $"CN=User {n:D6},OU=Staff,OU=Users,OU=Corporate,DC=panoply,DC=local";

    private static string GroupDn(int n) => $"CN=Group {n:D4},OU=Groups,OU=Corporate,DC=panoply,DC=local";

    private sealed record SeededObjects(
        int GuidAttributeId, int DnAttributeId, int[] TextAttributeIds,
        int GroupGuidAttributeId, int GroupDnAttributeId, int MemberAttributeId,
        Guid[] UserIds, Guid[] GroupIds);

    /// <summary>
    /// Writes every value row in one COPY, in the import writer's column list and value types
    /// (<c>SyncRepository.BulkInsertCsoAttributeValuesOnConnectionAsync</c>), streamed so nothing is held in memory.
    /// Value ids come from a fixed seed, so every run writes the same rows.
    /// </summary>
    private async Task<(double Seconds, long Rows)> CopyValuesAsync(SeededObjects seeded)
    {
        var random = new Random(2868);
        Guid NextId()
        {
            Span<byte> bytes = stackalloc byte[16];
            random.NextBytes(bytes);
            return new Guid(bytes);
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var stopwatch = Stopwatch.StartNew();
        long rows = 0;
        await using (var writer = await connection.BeginBinaryImportAsync(
            $"""COPY "ConnectedSystemObjectAttributeValues" ({BulkSqlHelpers.ToQuotedList(CsoBulkColumns.ConnectedSystemObjectAttributeValues)}) FROM STDIN (FORMAT binary)"""))
        {
            for (var n = 0; n < UserCount; n++)
            {
                var userId = seeded.UserIds[n];
                await WriteRowAsync(writer, NextId(), userId, seeded.GuidAttributeId, guidValue: NextId());
                await WriteRowAsync(writer, NextId(), userId, seeded.DnAttributeId, stringValue: UserDn(n));
                for (var t = 0; t < seeded.TextAttributeIds.Length; t++)
                    await WriteRowAsync(writer, NextId(), userId, seeded.TextAttributeIds[t], stringValue: $"{UserTextAttributes[t]} of user {n:D6}");
                rows += 2 + seeded.TextAttributeIds.Length;
            }

            for (var g = 0; g < GroupCount; g++)
            {
                var groupId = seeded.GroupIds[g];
                await WriteRowAsync(writer, NextId(), groupId, seeded.GroupGuidAttributeId, guidValue: NextId());
                await WriteRowAsync(writer, NextId(), groupId, seeded.GroupDnAttributeId, stringValue: GroupDn(g));
                for (var m = 0; m < MembersPerGroup; m++)
                {
                    // Distinct within a group (7 * MembersPerGroup < UserCount), spread across the users.
                    var member = (g * MembersPerGroup + m * 7) % UserCount;
                    await WriteRowAsync(writer, NextId(), groupId, seeded.MemberAttributeId,
                        referenceValueId: seeded.UserIds[member], unresolvedReferenceValue: UserDn(member));
                }

                rows += 2 + MembersPerGroup;
            }

            await writer.CompleteAsync();
        }

        return (stopwatch.Elapsed.TotalSeconds, rows);
    }

    private static async Task WriteRowAsync(NpgsqlBinaryImporter writer, Guid id, Guid csoId, int attributeId, string? stringValue = null,
        Guid? guidValue = null, Guid? referenceValueId = null, string? unresolvedReferenceValue = null)
    {
        // Order MUST match CsoBulkColumns.ConnectedSystemObjectAttributeValues.
        await writer.StartRowAsync();
        await writer.WriteAsync(id, NpgsqlTypes.NpgsqlDbType.Uuid);
        await writer.WriteAsync(csoId, NpgsqlTypes.NpgsqlDbType.Uuid);
        await writer.WriteAsync(attributeId, NpgsqlTypes.NpgsqlDbType.Integer);
        await WriteNullableAsync(writer, stringValue, NpgsqlTypes.NpgsqlDbType.Text);
        await writer.WriteNullAsync(); // DateTimeValue
        await writer.WriteNullAsync(); // IntValue
        await writer.WriteNullAsync(); // LongValue
        await writer.WriteNullAsync(); // DecimalValue
        await writer.WriteNullAsync(); // ByteValue
        await WriteNullableAsync(writer, guidValue, NpgsqlTypes.NpgsqlDbType.Uuid);
        await writer.WriteNullAsync(); // BoolValue
        await WriteNullableAsync(writer, referenceValueId, NpgsqlTypes.NpgsqlDbType.Uuid);
        await WriteNullableAsync(writer, unresolvedReferenceValue, NpgsqlTypes.NpgsqlDbType.Text);
    }

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter writer, string? value, NpgsqlTypes.NpgsqlDbType type)
    {
        if (value is null)
            await writer.WriteNullAsync();
        else
            await writer.WriteAsync(value, type);
    }

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter writer, Guid? value, NpgsqlTypes.NpgsqlDbType type)
    {
        if (value.HasValue)
            await writer.WriteAsync(value.Value, type);
        else
            await writer.WriteNullAsync();
    }

    private async Task<IReadOnlyDictionary<string, long>> IndexSizesAsync()
    {
        var sizes = new Dictionary<string, long>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT indexrelname::text, pg_relation_size(indexrelid)
            FROM pg_stat_user_indexes WHERE relname = 'ConnectedSystemObjectAttributeValues'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            sizes[reader.GetString(0)] = reader.GetInt64(1);
        return sizes;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The Connected System, its user and group types and their attributes, and every user and group object, without
    /// values: one of each written through EF, the rest copied from it in SQL so every column takes a valid value.
    /// </summary>
    private async Task<SeededObjects> SeedObjectsAsync()
    {
        int guidId, dnId, groupGuidId, groupDnId, memberId;
        int[] textIds;
        Guid templateUserId, templateGroupId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Panoply AD", ConnectorDefinition = connectorDefinition };
            var userType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var guid = Attribute(userType, "objectGUID", AttributeDataType.Guid, AttributePlurality.SingleValued, externalId: true);
            var dn = Attribute(userType, "distinguishedName", AttributeDataType.Text, AttributePlurality.SingleValued, secondaryExternalId: true);
            var texts = UserTextAttributes.Select(name => Attribute(userType, name, AttributeDataType.Text, AttributePlurality.SingleValued)).ToList();
            var groupType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
            var groupGuid = Attribute(groupType, "objectGUID", AttributeDataType.Guid, AttributePlurality.SingleValued, externalId: true);
            var groupDn = Attribute(groupType, "distinguishedName", AttributeDataType.Text, AttributePlurality.SingleValued, secondaryExternalId: true);
            var member = Attribute(groupType, "member", AttributeDataType.Reference, AttributePlurality.MultiValued);
            seed.AddRange(connectorDefinition, system, userType, groupType);
            await seed.SaveChangesAsync();

            var templateUser = new ConnectedSystemObject
            {
                Id = LargeGroupImportSeed.DeterministicGuid("u0"), Type = userType, ConnectedSystem = system,
                Status = ConnectedSystemObjectStatus.Normal, ExternalIdAttributeId = guid.Id, SecondaryExternalIdAttributeId = dn.Id
            };
            var templateGroup = new ConnectedSystemObject
            {
                Id = LargeGroupImportSeed.DeterministicGuid("g0"), Type = groupType, ConnectedSystem = system,
                Status = ConnectedSystemObjectStatus.Normal, ExternalIdAttributeId = groupGuid.Id, SecondaryExternalIdAttributeId = groupDn.Id
            };
            seed.AddRange(templateUser, templateGroup);
            await seed.SaveChangesAsync();

            (guidId, dnId, groupGuidId, groupDnId, memberId) = (guid.Id, dn.Id, groupGuid.Id, groupDn.Id, member.Id);
            textIds = texts.Select(t => t.Id).ToArray();
            (templateUserId, templateGroupId) = (templateUser.Id, templateGroup.Id);
        }

        await ExecuteAsync($"""
            INSERT INTO "ConnectedSystemObjects"
            SELECT (jsonb_populate_record(cso, jsonb_build_object('Id', md5('u' || n)::uuid))).*
            FROM "ConnectedSystemObjects" AS cso, generate_series(1, {UserCount - 1}) AS n
            WHERE cso."Id" = '{templateUserId}';

            INSERT INTO "ConnectedSystemObjects"
            SELECT (jsonb_populate_record(cso, jsonb_build_object('Id', md5('g' || n)::uuid))).*
            FROM "ConnectedSystemObjects" AS cso, generate_series(1, {GroupCount - 1}) AS n
            WHERE cso."Id" = '{templateGroupId}';

            ANALYZE "ConnectedSystemObjects";
            """);

        return new SeededObjects(guidId, dnId, textIds, groupGuidId, groupDnId, memberId,
            Enumerable.Range(0, UserCount).Select(n => LargeGroupImportSeed.DeterministicGuid("u" + n)).ToArray(),
            Enumerable.Range(0, GroupCount).Select(n => LargeGroupImportSeed.DeterministicGuid("g" + n)).ToArray());
    }

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
