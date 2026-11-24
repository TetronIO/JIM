// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using Activity = JIM.Models.Activities.Activity;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Measurement only (#3059, Phase 2): the cost of the Merge contribution mode against Priority, on real PostgreSQL through
/// the real Full Synchronisation processor. Three Connected Systems feed one multi-valued attribute with overlapping values
/// (A: a, b, c; B: b, c, d; C: c, d, e per person). Each mode runs the same three phases on a fresh database: the initial
/// synchronisation of all three systems, a steady-state re-synchronisation with nothing changed, and a churn run where 5% of
/// A's objects drop b, a value B still supplies (under Merge: a withdrawal, a re-election and a re-tag per object). The PRD's
/// budget is Merge within 10% of Priority.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
[Explicit("Benchmark (#3059): seeds three Connected Systems of several thousand objects per mode; run on its own.")]
public class MergeContributionModeBenchmarkDatabaseTests
{
    private const int People = 5_000;
    private const double ChurnFraction = 0.05;
    private const int SeedBatch = 1_000;
    private const int Repetitions = 3;

    private string _connectionString = null!;

    // JIM's own spans and Npgsql's span per command, totalled by name while a churn run is recorded, to show where its
    // time goes.
    private ActivityListener _listener = null!;
    private volatile bool _recording;
    private readonly ConcurrentQueue<(string Name, double Ms)> _spans = new();
    private readonly Dictionary<string, (int SystemId, int TypeId, int ExternalIdAttributeId, int EmailsAttributeId)> _systems = new();

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _listener?.Dispose();
        TestUtilities.SetEnvironmentVariables();
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the Merge contribution mode benchmark.");
        _connectionString = connectionString;

        TestUtilities.SetEnvironmentVariables();

        // The import's bulk writer opens parallel connections from JIM's own database settings, not from this fixture's
        // connection string; point them at the same database, as a deployment would, so seeding runs the production path.
        var target = new Npgsql.NpgsqlConnectionStringBuilder(_connectionString);
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseHostname, $"{target.Host}:{target.Port}");
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseName, target.Database);
        Environment.SetEnvironmentVariable(Constants.Config.DatabaseUsername, target.Username);
        Environment.SetEnvironmentVariable(Constants.Config.DatabasePassword, target.Password);

        using var ctx = NewContext();
        ctx.Database.Migrate();

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("JIM.", StringComparison.Ordinal) || source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span =>
            {
                if (_recording)
                    _spans.Enqueue((span.Source.Name == "Npgsql" ? "(database command)" : span.DisplayName, span.Duration.TotalMilliseconds));
            }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [Test]
    public async Task MergeAgainstPriority_InitialSteadyAndChurn_ReportsTheCostAsync()
    {
        // A discarded warm-up, so neither mode pays the process's first-run costs (JIT, connection pools) in its figures;
        // then the modes alternate, and each phase reports its median, so neither drift nor one slow run decides it.
        await MeasureAsync(AttributeContributionMode.Priority);
        var priorityRuns = new List<Measurement>();
        var mergeRuns = new List<Measurement>();
        for (var run = 0; run < Repetitions; run++)
        {
            priorityRuns.Add(await MeasureAsync(AttributeContributionMode.Priority));
            mergeRuns.Add(await MeasureAsync(AttributeContributionMode.Merge));
        }

        static long Median(IEnumerable<long> values) => values.OrderBy(v => v).ElementAt(Repetitions / 2);
        var priority = priorityRuns[^1] with
        {
            Initial = Median(priorityRuns.Select(r => r.Initial)), Steady = Median(priorityRuns.Select(r => r.Steady)), Churn = Median(priorityRuns.Select(r => r.Churn))
        };
        var merge = mergeRuns[^1] with
        {
            Initial = Median(mergeRuns.Select(r => r.Initial)), Steady = Median(mergeRuns.Select(r => r.Steady)), Churn = Median(mergeRuns.Select(r => r.Churn))
        };
        TestContext.Out.WriteLine($"  Runs (initial/steady/churn ms) Priority: {string.Join("  ", priorityRuns.Select(r => $"{r.Initial}/{r.Steady}/{r.Churn}"))}");
        TestContext.Out.WriteLine($"  Runs (initial/steady/churn ms) Merge:    {string.Join("  ", mergeRuns.Select(r => $"{r.Initial}/{r.Steady}/{r.Churn}"))}");

        TestContext.Out.WriteLine($"Merge contribution mode benchmark: {People:N0} people, three Connected Systems, three overlapping values each, " +
                                  $"churn {ChurnFraction:P0} of A dropping a value B still supplies; median of {Repetitions} alternating runs after a warm-up.");
        foreach (var (phase, p, m) in new[]
                 {
                     ("Initial (A, B, C)", priority.Initial, merge.Initial),
                     ("Steady state (A, B, C)", priority.Steady, merge.Steady),
                     ("Churn (A)", priority.Churn, merge.Churn)
                 })
        {
            TestContext.Out.WriteLine($"  {phase,-24} Priority {p,7:N0} ms   Merge {m,7:N0} ms   Merge/Priority {(double)m / p,6:P1}");
        }

        foreach (var (phase, select) in new (string, Func<Measurement, IReadOnlyList<(string Name, int Count, double Ms)>>)[]
                     { ("Steady state", m => m.SteadySpans), ("Churn", m => m.ChurnSpans) })
        {
            foreach (var (mode, measurement) in new[] { ("Priority", priority), ("Merge", merge) })
            {
                TestContext.Out.WriteLine($"  {phase} run, {mode}: where the time went (span totals; spans nest, so they overlap)");
                foreach (var (name, count, ms) in select(measurement).Take(14))
                    TestContext.Out.WriteLine($"    {name,-60} {count,7:N0} x {ms,9:N0} ms");
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(priority.ValuesAfterChurn, Is.EqualTo(People * 3 - (int)(People * ChurnFraction)), "Priority holds A's values only");
            Assert.That(merge.ValuesAfterChurn, Is.EqualTo(People * 5), "Merge holds every distinct value, b kept for the churned people");
            Assert.That(merge.RetaggedToB, Is.EqualTo((int)(People * ChurnFraction)), "each churned person's b is credited to B");
        }
    }

    private sealed record Measurement(long Initial, long Steady, long Churn, int ValuesAfterChurn, int RetaggedToB,
        IReadOnlyList<(string Name, int Count, double Ms)> SteadySpans, IReadOnlyList<(string Name, int Count, double Ms)> ChurnSpans);

    /// <summary>Runs <paramref name="run"/> with span recording on, returning its time and its spans totalled by name.</summary>
    private async Task<(long Ms, IReadOnlyList<(string Name, int Count, double Ms)> Spans)> RecordAsync(Func<Task<long>> run)
    {
        _spans.Clear();
        _recording = true;
        long ms;
        try
        {
            ms = await run();
        }
        finally
        {
            _recording = false;
        }

        return (ms, _spans.GroupBy(span => span.Name)
            .Select(group => (Name: group.Key, Count: group.Count(), Ms: group.Sum(span => span.Ms)))
            .OrderByDescending(entry => entry.Ms)
            .ToList());
    }

    private async Task<Measurement> MeasureAsync(AttributeContributionMode mode)
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        _systems.Clear();
        var emailsAttributeId = await SeedConfigurationAsync(mode);
        await SeedObjectsAsync();

        var initial = await TimeAsync("A", "B", "C");
        var (steady, steadySpans) = await RecordAsync(() => TimeAsync("A", "B", "C"));
        await ChurnAsync();
        var (churn, churnSpans) = await RecordAsync(() => TimeAsync("A"));

        await using var read = NewContext();
        var values = await read.MetaverseObjectAttributeValues.AsNoTracking().CountAsync(av => av.AttributeId == emailsAttributeId);
        var retagged = await read.MetaverseObjectAttributeValues.AsNoTracking()
            .CountAsync(av => av.AttributeId == emailsAttributeId && av.StringValue!.EndsWith("-b") && av.ContributedBySystemId == _systems["B"].SystemId);
        return new Measurement(initial, steady, churn, values, retagged, steadySpans, churnSpans);
    }

    private async Task<long> TimeAsync(params string[] systems)
    {
        var stopwatch = Stopwatch.StartNew();
        foreach (var system in systems)
            await RunFullSynchronisationAsync(_systems[system].SystemId);
        return stopwatch.ElapsedMilliseconds;
    }

    /// <summary>Three systems feeding "Email Addresses" in <paramref name="mode"/>; A projects, B and C join on the employee id.</summary>
    private async Task<int> SeedConfigurationAsync(AttributeContributionMode mode)
    {
        await using var seed = NewContext();
        foreach (var (key, valueType, value) in new[]
                 {
                     (Constants.SettingKeys.SyncPageSize, ServiceSettingValueType.Integer, "500"),
                     ("ChangeTracking.CsoChanges.Enabled", ServiceSettingValueType.Boolean, "false"),
                     ("ChangeTracking.MvoChanges.Enabled", ServiceSettingValueType.Boolean, "true")
                 })
        {
            seed.ServiceSettingItems.Add(new ServiceSetting
            {
                Key = key, DisplayName = key, Category = ServiceSettingCategory.Synchronisation, ValueType = valueType, DefaultValue = value, Value = value
            });
        }

        var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People", DeletionRule = MetaverseObjectDeletionRule.Manual };
        var mvEmployeeId = new MetaverseAttribute
        {
            Name = "Employee ID", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, MetaverseObjectTypes = [mvType]
        };
        var mvEmails = new MetaverseAttribute
        {
            Name = "Email Addresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued,
            ContributionMode = mode, MetaverseObjectTypes = [mvType]
        };
        var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
        seed.AddRange(connectorDefinition, mvType, mvEmployeeId, mvEmails);
        await seed.SaveChangesAsync();

        var priority = 1;
        foreach (var name in new[] { "A", "B", "C" })
        {
            var system = new ConnectedSystem { Name = name, ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule };
            var type = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
            var employeeId = new ConnectedSystemObjectTypeAttribute
            {
                Name = "employeeId", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued,
                Selected = true, IsExternalId = true
            };
            var emails = new ConnectedSystemObjectTypeAttribute
            {
                Name = "emails", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued,
                Selected = true
            };
            type.Attributes.Add(employeeId);
            type.Attributes.Add(emails);
            seed.AddRange(system, type);
            await seed.SaveChangesAsync();
            seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
                { ConnectedSystemId = system.Id, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation });

            var projects = name == "A";
            var rule = new SyncRule
            {
                Name = $"{name} Import", ConnectedSystem = system, Direction = SyncRuleDirection.Import, Enabled = true,
                ConnectedSystemObjectType = type, MetaverseObjectType = mvType, ProjectToMetaverse = projects
            };
            if (projects)
            {
                rule.AttributeFlowRules.Add(new SyncRuleMapping
                {
                    SyncRule = rule, TargetMetaverseAttribute = mvEmployeeId,
                    Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = employeeId } }
                });
            }
            else
            {
                rule.ObjectMatchingRules.Add(new ObjectMatchingRule
                {
                    SyncRule = rule, Order = 0, CaseSensitive = true, TargetMetaverseAttribute = mvEmployeeId,
                    Sources = new List<ObjectMatchingRuleSource> { new() { Order = 0, ConnectedSystemAttribute = employeeId } }
                });
            }
            rule.AttributeFlowRules.Add(new SyncRuleMapping
            {
                SyncRule = rule, TargetMetaverseAttribute = mvEmails, Priority = priority++,
                Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = emails } }
            });
            seed.SyncRules.Add(rule);
            await seed.SaveChangesAsync();
            _systems[name] = (system.Id, type.Id, employeeId.Id, emails.Id);
        }

        return mvEmails.Id;
    }

    private static readonly Dictionary<string, string[]> Suffixes = new()
    {
        ["A"] = ["a", "b", "c"], ["B"] = ["b", "c", "d"], ["C"] = ["c", "d", "e"]
    };

    private async Task SeedObjectsAsync()
    {
        foreach (var (name, (systemId, typeId, externalIdAttributeId, emailsAttributeId)) in _systems)
        {
            for (var start = 0; start < People; start += SeedBatch)
            {
                await using var ctx = NewContext();
                var batch = Enumerable.Range(start, Math.Min(SeedBatch, People - start)).Select(person =>
                {
                    var cso = new ConnectedSystemObject
                    {
                        Id = Guid.NewGuid(), ConnectedSystemId = systemId, TypeId = typeId, ExternalIdAttributeId = externalIdAttributeId,
                        Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow.AddHours(-2)
                    };
                    cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalIdAttributeId, StringValue = $"E{person}" });
                    cso.AttributeValues.AddRange(Suffixes[name].Select(suffix => new ConnectedSystemObjectAttributeValue
                        { Id = Guid.NewGuid(), AttributeId = emailsAttributeId, StringValue = $"{person}-{suffix}" }));
                    return cso;
                }).ToList();
                await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync(batch);
            }
        }
    }

    /// <summary>A's first 5% of people drop b, through the import's own update writer.</summary>
    private async Task ChurnAsync()
    {
        var (systemId, _, externalIdAttributeId, emailsAttributeId) = _systems["A"];
        var churned = Enumerable.Range(0, (int)(People * ChurnFraction)).Select(person => $"E{person}").ToHashSet();
        await using var ctx = NewContext();
        var csos = await ctx.ConnectedSystemObjects.AsNoTracking().Include(c => c.AttributeValues)
            .Where(c => c.ConnectedSystemId == systemId)
            .ToListAsync();
        var targets = csos.Where(c => churned.Contains(c.AttributeValues.Single(av => av.AttributeId == externalIdAttributeId).StringValue!)).ToList();
        var removals = targets.Select(c => c.AttributeValues.Single(av => av.AttributeId == emailsAttributeId && av.StringValue!.EndsWith("-b", StringComparison.Ordinal)).Id).ToList();
        foreach (var cso in targets)
            cso.LastUpdated = DateTime.UtcNow;
        await new PostgresDataRepository(ctx).Sync.UpdateConnectedSystemObjectsAsync(targets, [], removals);
    }

    private async Task RunFullSynchronisationAsync(int systemId)
    {
        await using var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);
        var system = await jim.ConnectedSystems.GetConnectedSystemAsync(systemId, withChangeTracking: true)
            ?? throw new InvalidOperationException("Connected System not found.");
        var runProfile = system.RunProfiles!.Single(rp => rp.RunType == ConnectedSystemRunType.FullSynchronisation);
        var activity = new Activity
        {
            TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = ConnectedSystemRunType.FullSynchronisation
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();

        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
            system, runProfile, activity, new CancellationTokenSource()).PerformFullSyncAsync();

        await using var read = NewContext();
        var errors = await read.ActivityRunProfileExecutionItems.AsNoTracking()
            .Where(i => i.ActivityId == activity.Id && i.ErrorType != null && i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .Select(i => i.ErrorMessage)
            .Take(3)
            .ToListAsync();
        Assert.That(errors, Is.Empty, $"the run reported errors: {string.Join("; ", errors)}");
    }
}
