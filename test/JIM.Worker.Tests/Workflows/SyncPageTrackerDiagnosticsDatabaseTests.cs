// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using Serilog;
using Serilog.Events;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Real-PostgreSQL synchronisation runs that read the line each page boundary logs. The line gives the change tracker's
/// size after the page's clear, which says only that the clear happened; the size before it is what the page
/// accumulated, which is what a scale run needs to see (#2669). Only a real database shows it: the in-memory repository
/// has no tracker.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncPageTrackerDiagnosticsDatabaseTests
{
    private const int PageSize = 2;
    private const int Objects = 3;

    private string _connectionString = null!;
    private int _systemId;
    private int _externalIdAttributeId;
    private int _displayNameAttributeId;

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        // The Worker tracks by default (src/JIM.Worker/Program.cs); a synchronisation run must too.
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL page diagnostics tests.");
        _connectionString = connectionString!;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        await SeedAsync();
    }

    [Test]
    public async Task FullSync_EachPage_LogsTheTrackerSizeBeforeItsClearAsync()
    {
        var pages = await RunCapturingPageLinesAsync(ConnectedSystemRunType.FullSynchronisation);

        AssertEachPageReportsItsTrackerBeforeTheClear(pages);
    }

    [Test]
    public async Task DeltaSync_EachPage_LogsTheTrackerSizeBeforeItsClearAsync()
    {
        await RunCapturingPageLinesAsync(ConnectedSystemRunType.FullSynchronisation);
        await MarkEveryObjectForSynchronisationAsync();

        var pages = await RunCapturingPageLinesAsync(ConnectedSystemRunType.DeltaSynchronisation);

        AssertEachPageReportsItsTrackerBeforeTheClear(pages);
    }

    /// <summary>
    /// Every page held its own objects and their values tracked until its clear, so the count before the clear is at
    /// least the smallest page's (one object, two values) and more than what is left after it.
    /// </summary>
    private static void AssertEachPageReportsItsTrackerBeforeTheClear(List<LogEvent> pages)
    {
        Assert.That(pages, Has.Count.EqualTo(2), $"{Objects} objects at {PageSize} a page cross one page boundary");
        Assert.That(pages.Select(p => p.Properties.ContainsKey("TrackerCountBeforeClear")), Is.All.True,
            "each page's line gives the count before the clear");
        using (Assert.EnterMultipleScope())
        {
            foreach (var (before, after) in pages.Select(p => (IntProperty(p, "TrackerCountBeforeClear"), IntProperty(p, "TrackerCount"))))
            {
                Assert.That(before, Is.GreaterThanOrEqualTo(3), "the page's objects and values, tracked until the clear");
                Assert.That(before, Is.GreaterThan(after), "the count before the clear is the page's, not what survives it");
            }
        }
    }

    private static int IntProperty(LogEvent logEvent, string name) =>
        (int)((ScalarValue)logEvent.Properties[name]).Value!;

    /// <summary>
    /// One Connected System of <see cref="Objects"/> people, each with an external id and a display name, whose import
    /// Synchronisation Rule projects every one; page size <see cref="PageSize"/>; change tracking off.
    /// </summary>
    private async Task SeedAsync()
    {
        await using (var seed = NewContext())
        {
            foreach (var (key, valueType, value) in new[]
                     {
                         (Constants.SettingKeys.SyncPageSize, ServiceSettingValueType.Integer, PageSize.ToString()),
                         ("ChangeTracking.CsoChanges.Enabled", ServiceSettingValueType.Boolean, "false"),
                         ("ChangeTracking.MvoChanges.Enabled", ServiceSettingValueType.Boolean, "false")
                     })
            {
                seed.ServiceSettingItems.Add(new ServiceSetting
                {
                    Key = key, DisplayName = key, Category = ServiceSettingCategory.Synchronisation,
                    ValueType = valueType, DefaultValue = value, Value = value
                });
            }

            var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
            var system = new ConnectedSystem
            {
                Name = "HR", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
            };
            var type = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
            var externalId = new ConnectedSystemObjectTypeAttribute
            {
                Name = "employeeId", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
            };
            var displayName = new ConnectedSystemObjectTypeAttribute
            {
                Name = "displayName", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true
            };
            type.Attributes.Add(externalId);
            type.Attributes.Add(displayName);

            var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
            var mvDisplayName = new MetaverseAttribute
            {
                Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued,
                MetaverseObjectTypes = [mvType]
            };

            seed.AddRange(connectorDefinition, system, type, mvType, mvDisplayName);
            await seed.SaveChangesAsync();
            seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
                { ConnectedSystemId = system.Id, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation });
            seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
                { ConnectedSystemId = system.Id, Name = "Delta Synchronisation", RunType = ConnectedSystemRunType.DeltaSynchronisation });

            var rule = new SyncRule
            {
                Name = "HR Import", ConnectedSystem = system, Direction = SyncRuleDirection.Import, Enabled = true,
                ConnectedSystemObjectType = type, MetaverseObjectType = mvType, ProjectToMetaverse = true
            };
            rule.AttributeFlowRules.Add(new SyncRuleMapping
            {
                SyncRule = rule, TargetMetaverseAttribute = mvDisplayName,
                Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = displayName } }
            });
            seed.SyncRules.Add(rule);
            await seed.SaveChangesAsync();

            (_systemId, _externalIdAttributeId, _displayNameAttributeId) = (system.Id, externalId.Id, displayName.Id);
        }

        await using var ctx = NewContext();
        var typeId = await ctx.ConnectedSystemObjectTypes.Where(t => t.ConnectedSystemId == _systemId).Select(t => t.Id).SingleAsync();
        var objects = Enumerable.Range(1, Objects).Select(n =>
        {
            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(), ConnectedSystemId = _systemId, TypeId = typeId, ExternalIdAttributeId = _externalIdAttributeId,
                Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow.AddHours(-2)
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _externalIdAttributeId, StringValue = $"E{n}" });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _displayNameAttributeId, StringValue = $"Name {n}" });
            return cso;
        }).ToList();
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync(objects);
    }

    /// <summary>
    /// Marks every object for synchronisation, as an import that changed each would, so a Delta Synchronisation selects
    /// them all.
    /// </summary>
    private async Task MarkEveryObjectForSynchronisationAsync()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(
            @"UPDATE ""ConnectedSystemObjects"" SET ""SynchronisationPending"" = true WHERE ""ConnectedSystemId"" = {0}", _systemId);
    }

    /// <summary>
    /// Runs one synchronisation on its own tracking context, as the Worker does, and returns the line each page boundary
    /// logged.
    /// </summary>
    private async Task<List<LogEvent>> RunCapturingPageLinesAsync(ConnectedSystemRunType runType)
    {
        var sink = new CapturingSink();
        var original = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        try
        {
            await using var ctx = NewContext(trackAll: true);
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            var system = await jim.ConnectedSystems.GetConnectedSystemAsync(_systemId, withChangeTracking: true)
                ?? throw new InvalidOperationException("Connected System not found.");
            var runProfile = system.RunProfiles!.Single(rp => rp.RunType == runType);
            var activity = new Activity
            {
                TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
                ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = runType
            };
            ctx.Activities.Add(activity);
            await ctx.SaveChangesAsync();

            if (runType == ConnectedSystemRunType.FullSynchronisation)
            {
                await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
                    system, runProfile, activity, new CancellationTokenSource()).PerformFullSyncAsync();
            }
            else
            {
                await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
                    system, runProfile, activity, new CancellationTokenSource()).PerformDeltaSyncAsync();
            }
        }
        finally
        {
            Log.Logger = original;
        }

        return sink.Events.Where(e => e.MessageTemplate.Text.StartsWith("Page {Page}/{TotalPages} complete.", StringComparison.Ordinal)).ToList();
    }

    private sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public List<LogEvent> Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
                _events.Add(logEvent);
        }
    }
}
