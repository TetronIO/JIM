// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL check that the change record reference fix-up reports itself as spans (#2690): a
/// <c>Cso.ResolveChangeReferences</c> span carrying how many resolutions it found and how many it applied, with a
/// child for finding them and a child for applying them, so its cost shows in the Worker's diagnostic breakdown after
/// every export and import.
/// </summary>
/// <remarks>
/// Spans are read through the trace of a span the test starts itself, so a fix-up elsewhere in the run cannot be
/// mistaken for this one. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ChangeRecordReferenceFixupSpanDatabaseTests
{
    private static readonly ActivitySource TestSource = new("JIM.Tests.ChangeRecordReferenceFixupSpans");

    private string _connectionString = null!;
    private ActivityListener _listener = null!;
    private readonly ConcurrentQueue<Activity> _stopped = new();

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL change record reference fix-up span tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        ctx.Database.Migrate();

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("JIM.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Enqueue
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _listener?.Dispose();

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [Test]
    public async Task FixupCrossBatchChangeRecordReferenceIdsAsync_OneResolvableOfTwoReferences_ReportsResolveSpanWithCountsAndAChildPerStepAsync()
    {
        var systemId = await SeedOneResolvableAndOneUnresolvableReferenceAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var spans = await RecordAsync(() => repository.Sync.FixupCrossBatchChangeRecordReferenceIdsAsync(systemId));

        var resolve = spans.Single(s => s.DisplayName == "Cso.ResolveChangeReferences");
        var children = spans.Where(s => s.ParentSpanId == resolve.SpanId).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolve.GetTagItem("candidateCount"), Is.EqualTo(1), "the parent span counts the resolutions found");
            Assert.That(resolve.GetTagItem("resolvedCount"), Is.EqualTo(1), "the parent span counts the resolutions applied");
            Assert.That(children.Select(s => s.DisplayName), Is.EquivalentTo(new[]
            {
                "Cso.ResolveChangeReferences.FindCandidates", "Cso.ResolveChangeReferences.Apply"
            }), "one child for finding the resolutions and one for applying them");
            var find = children.Single(s => s.DisplayName == "Cso.ResolveChangeReferences.FindCandidates");
            Assert.That(find.GetTagItem("markedCount"), Is.EqualTo(2), "the finding step reports how many marked values it read");
            Assert.That(find.GetTagItem("candidateCount"), Is.EqualTo(1), "the finding step reports what it found");
            Assert.That(children.Single(s => s.DisplayName == "Cso.ResolveChangeReferences.Apply").GetTagItem("resolvedCount"),
                Is.EqualTo(1), "the applying step reports what it resolved");
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> under a span of the test's own and returns every span completed in its trace.
    /// </summary>
    private async Task<List<Activity>> RecordAsync(Func<Task> action)
    {
        ActivityTraceId traceId;
        using (var root = TestSource.StartActivity("Test"))
        {
            Assert.That(root, Is.Not.Null, "the test's listener samples its own source");
            traceId = root!.TraceId;
            await action();
        }
        return [.. _stopped.Where(s => s.TraceId == traceId)];
    }

    /// <summary>
    /// A person whose Secondary External ID is a DN, and a change record written through the repository's writer with
    /// two references held as DNs alone: one naming that person and one naming nobody.
    /// </summary>
    private async Task<int> SeedOneResolvableAndOneUnresolvableReferenceAsync()
    {
        const string managerDn = "CN=Mo Manager,OU=People,DC=glitterband,DC=local";

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
        var dn = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsSecondaryExternalId = true
        };
        var manager = new ConnectedSystemObjectTypeAttribute
        {
            Name = "manager", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        objectType.Attributes.AddRange([dn, manager]);

        await using (var seed = NewContext())
        {
            seed.AddRange(connectorDefinition, system, objectType);
            await seed.SaveChangesAsync();

            var person = new ConnectedSystemObject
            {
                ConnectedSystemId = system.Id, TypeId = objectType.Id, ExternalIdAttributeId = dn.Id,
                SecondaryExternalIdAttributeId = dn.Id, Status = ConnectedSystemObjectStatus.Normal
            };
            person.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = person, AttributeId = dn.Id, StringValue = managerDn });
            seed.Add(person);
            await seed.SaveChangesAsync();
        }

        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = system.Id,
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Added,
            InitiatedByType = JIM.Models.Activities.ActivityInitiatorType.System
        };
        var attributeChange = new ConnectedSystemObjectChangeAttribute
        {
            Attribute = manager, AttributeName = manager.Name, AttributeType = manager.Type, ConnectedSystemChange = change
        };
        change.AttributeChanges.Add(attributeChange);
        attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, ValueChangeType.Add, managerDn));
        attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(
            attributeChange, ValueChangeType.Add, "CN=Nobody,OU=People,DC=glitterband,DC=local"));

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.PersistCsoChangesAsync([change]);

        return system.Id;
    }
}
