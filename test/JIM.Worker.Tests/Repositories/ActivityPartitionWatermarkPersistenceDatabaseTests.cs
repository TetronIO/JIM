// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Models.Activities;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of how an import Activity holds the Partition watermarks it started from and recorded
/// (#2715): each column is a JSON array with one <c>{ partitionId, partitionName, value }</c> object per Partition. The
/// Activity page, the REST API and PowerShell all read that shape, so it is pinned here at the column, through the same
/// update the Worker makes. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ActivityPartitionWatermarkPersistenceDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Activity Partition watermark tests.");

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

    [Test]
    public async Task UpdateActivityAsync_PartitionWatermarks_StoresOnePartitionIdNameAndValueObjectPerPartitionAsync()
    {
        var activityId = await CreateImportActivityAsync();
        List<ImportPartitionWatermark> from =
        [
            new() { PartitionId = 7, PartitionName = "corp.local", Value = "{\"usn\":41}" },
            new() { PartitionId = 8, PartitionName = "partners.local", Value = null }
        ];
        List<ImportPartitionWatermark> to =
        [
            new() { PartitionId = 7, PartitionName = "corp.local", Value = null },
            new() { PartitionId = 8, PartitionName = "partners.local", Value = "{\"usn\":57}" }
        ];

        // The Worker's update: an Activity loaded on an earlier context, saved through the repository.
        await using (var ctx = NewContext())
        {
            var activity = await ctx.Activities.SingleAsync(a => a.Id == activityId);
            activity.ImportPartitionWatermarksFrom = from;
            activity.ImportPartitionWatermarksTo = to;
            await new PostgresDataRepository(ctx).Activity.UpdateActivityAsync(activity);
        }

        var (fromJson, toJson) = await ReadColumnsAsync(activityId);
        await using var read = NewContext();
        var stored = await read.Activities.SingleAsync(a => a.Id == activityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Shape(fromJson), Is.EqualTo(new[] { (7, "corp.local", (string?)"{\"usn\":41}"), (8, "partners.local", null) }));
            Assert.That(Shape(toJson), Is.EqualTo(new[] { (7, "corp.local", (string?)null), (8, "partners.local", "{\"usn\":57}") }));
            Assert.That(Describe(stored.ImportPartitionWatermarksFrom), Is.EqualTo(Describe(from)));
            Assert.That(Describe(stored.ImportPartitionWatermarksTo), Is.EqualTo(Describe(to)));
        }
    }

    [Test]
    public async Task UpdateActivityAsync_NoPartitionWatermarks_LeavesBothColumnsNullAsync()
    {
        // An import whose Connector keeps no Partition watermarks, or one that recorded none.
        var activityId = await CreateImportActivityAsync();

        var (fromJson, toJson) = await ReadColumnsAsync(activityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromJson, Is.Null);
            Assert.That(toJson, Is.Null);
        }
    }

    #region Helpers

    private async Task<Guid> CreateImportActivityAsync()
    {
        await using var ctx = NewContext();
        var activity = new Activity { TargetName = "Delta Import", TargetType = ActivityTargetType.ConnectedSystemRunProfile, Message = "Importing" };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        return activity.Id;
    }

    /// <summary>The two columns' text, read with plain SQL so nothing between the column and the test reshapes it.</summary>
    private async Task<(string? From, string? To)> ReadColumnsAsync(Guid activityId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """SELECT "ImportPartitionWatermarksFrom", "ImportPartitionWatermarksTo" FROM "Activities" WHERE "Id" = @id""", connection);
        command.Parameters.AddWithValue("id", activityId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.That(await reader.ReadAsync(), Is.True, "precondition: the Activity was saved");
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>
    /// Reads a column's JSON strictly: an array of objects, each with exactly <c>partitionId</c>, <c>partitionName</c>
    /// and <c>value</c>, named as the REST API names them.
    /// </summary>
    private static List<(int PartitionId, string PartitionName, string? Value)> Shape(string? json)
    {
        Assert.That(json, Is.Not.Null, "the column holds the watermarks");
        using var document = JsonDocument.Parse(json!);
        Assert.That(document.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
        var entries = new List<(int, string, string?)>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            Assert.That(element.EnumerateObject().Select(property => property.Name), Is.EquivalentTo(new[] { "partitionId", "partitionName", "value" }));
            var value = element.GetProperty("value");
            entries.Add((element.GetProperty("partitionId").GetInt32(), element.GetProperty("partitionName").GetString()!,
                value.ValueKind == JsonValueKind.Null ? null : value.GetString()));
        }

        return entries;
    }

    private static List<(int PartitionId, string PartitionName, string? Value)>? Describe(List<ImportPartitionWatermark>? watermarks) =>
        watermarks?.Select(w => (w.PartitionId, w.PartitionName, w.Value)).ToList();

    #endregion
}
