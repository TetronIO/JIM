// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
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
/// Measures the Pending Export create (#2919) on one connection against its parallel path, for the two page shapes the
/// 500,000-object Full Synchronisation stages: a people page of 500 exports with 12 changes each, and a group page of 500
/// exports carrying about 87,000 member changes, most of them on one very large group. Each shape is written at 1, 4 and
/// 16 connections (the parallel path forced at 4 and 16 whatever the row threshold would decide), and the median of five
/// runs reported. Explicit: run by hand, against <c>JIM_TEST_RESET_*</c>.
/// </summary>
[TestFixture]
[Explicit("Benchmark; run by hand.")]
[Category("RequiresPostgres")]
public class PendingExportCreateBenchmarkDatabaseTests
{
    private const int Runs = 5;
    private const int PageSize = 500;
    private static readonly DateTime StagedAt = new(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;
    private Seeded _seeded = null!;

    private sealed record Seeded(int SystemId, List<ConnectedSystemObjectTypeAttribute> Texts, ConnectedSystemObjectTypeAttribute Member, List<Guid> Objects);

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the Pending Export create benchmark.");
        _connectionString = connectionString!;

        await using (var ctx = NewContext())
            await ctx.Database.MigrateAsync();

        await PostgresTestDatabase.ResetAsync(_connectionString);
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "object", ConnectedSystem = system, Selected = true };
        var texts = Enumerable.Range(0, 12).Select(i => new ConnectedSystemObjectTypeAttribute
        {
            Name = $"text{i}", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        }).ToList();
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        csType.Attributes.AddRange(texts);
        csType.Attributes.Add(member);
        var objects = Enumerable.Range(0, PageSize)
            .Select(_ => new ConnectedSystemObject { Id = Guid.NewGuid(), Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal, Created = StagedAt })
            .ToList();
        seed.AddRange(connectorDefinition, system, csType);
        seed.AddRange(objects);
        await seed.SaveChangesAsync();
        _seeded = new Seeded(system.Id, texts, member, objects.Select(o => o.Id).ToList());
    }

    private PendingExport NewExport(int index) => new()
    {
        Id = Guid.NewGuid(), ConnectedSystemId = _seeded.SystemId, ConnectedSystemObjectId = _seeded.Objects[index],
        ChangeType = PendingExportChangeType.Update, Status = PendingExportStatus.Pending, CreatedAt = StagedAt
    };

    /// <summary>A people page: 500 exports with 12 single-valued changes each.</summary>
    private List<PendingExport> PeoplePage() =>
        Enumerable.Range(0, PageSize).Select(i =>
        {
            var export = NewExport(i);
            foreach (var text in _seeded.Texts)
                export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(), AttributeId = text.Id, ChangeType = PendingExportAttributeChangeType.Update, StringValue = $"Person {i} {text.Name}"
                });
            return export;
        }).ToList();

    /// <summary>A group page: 500 exports, one adding 77,000 members and the rest 20 each (86,980 changes).</summary>
    private List<PendingExport> GroupPage() =>
        Enumerable.Range(0, PageSize).Select(i =>
        {
            var export = NewExport(i);
            var members = i == 0 ? 77_000 : 20;
            for (var m = 0; m < members; m++)
                export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(), AttributeId = _seeded.Member.Id, ChangeType = PendingExportAttributeChangeType.Add,
                    StringValue = $"CN=Member {m},OU=People,DC=glitterband,DC=local", ResolvedReferenceCsoId = _seeded.Objects[m % PageSize]
                });
            return export;
        }).ToList();

    private async Task EmptyPendingExportsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""TRUNCATE "PendingExportAttributeValueChanges", "PendingExports" """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<double> TimeOneAsync(int parallelism, Func<List<PendingExport>> build)
    {
        await EmptyPendingExportsAsync();
        var page = build();
        await using var context = NewContext();
        var repository = (SyncRepository)new PostgresDataRepository(context).Sync;
        repository.ConnectionStringForParallelWrites = _connectionString;
        repository.WriteParallelismOverride = parallelism;
        repository.ParallelWriteRowThreshold = 0;
        repository.ParallelWriteRowsPerConnection = 1; // exactly the connections measured, however few the rows (#2669)
        Assert.That(repository.ShouldWriteInParallel(page.Sum(pe => 1 + pe.AttributeValueChanges.Count)), Is.EqualTo(parallelism > 1));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await repository.CreatePendingExportsAsync(page);
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private async Task MeasureAsync(string shape, Func<List<PendingExport>> build)
    {
        int[] connections = [1, 4, 16];
        var results = connections.ToDictionary(c => c, _ => new List<double>());
        await TimeOneAsync(1, build); // warm-up, not counted
        for (var run = 0; run < Runs; run++)
        {
            // Rotate which goes first, so none always meets a warm or a cold cache.
            foreach (var c in connections.Skip(run % connections.Length).Concat(connections.Take(run % connections.Length)))
                results[c].Add(await TimeOneAsync(c, build));
        }

        static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
        var single = Median(results[1]);
        foreach (var c in connections)
            TestContext.Out.WriteLine($"{shape}, {c} connection(s): median {Median(results[c]):F0} ms " +
                $"[{string.Join(", ", results[c].Select(v => v.ToString("F0")))}], {single / Median(results[c]):F2}x");
    }

    [Test]
    public async Task PeoplePage_OneAgainstFourAgainstSixteenConnectionsAsync() =>
        await MeasureAsync("People page (500 exports x 12 changes)", PeoplePage);

    [Test]
    public async Task GroupPage_OneAgainstFourAgainstSixteenConnectionsAsync() =>
        await MeasureAsync("Group page (500 exports, 86,980 changes, one of 77,000)", GroupPage);
}
