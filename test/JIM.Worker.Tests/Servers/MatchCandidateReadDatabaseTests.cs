// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of how an ambiguous match's candidates are read back for an Activity item's detail
/// (#2052): in the order they were recorded, each checked for whether it still exists, and each Metaverse Object with
/// what is joined to it now. Run on a context that does not track, as JIM.Web's does.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MatchCandidateReadDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL match candidate read tests.");

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
    public async Task GetMatchCandidatesAsync_TwoIdentitiesOneSinceDeleted_ReadsBothInOrderWithWhatTheLiveOneIsJoinedToAsync()
    {
        var seeded = await SeedAsync();
        var deletedId = Guid.NewGuid();
        var itemId = await RecordAmbiguityAsync(
            Candidate(MatchCandidateKind.MetaverseObject, deletedId, "Ann Bloggs", ordinal: 0),
            Candidate(MatchCandidateKind.MetaverseObject, seeded.MvoId, "Zoe Bloggs", ordinal: 1));

        var candidates = await NewJim().Activities.GetMatchCandidatesAsync(itemId);

        Assert.That(candidates.Select(c => c.Candidate.CandidateId), Is.EqualTo(new[] { deletedId, seeded.MvoId }), "in the order recorded");
        var deleted = candidates[0];
        var live = candidates[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted.Exists, Is.False);
            Assert.That(deleted.Candidate.DisplayNameSnapshot, Is.EqualTo("Ann Bloggs"), "a deleted candidate still reads by its recorded name");
            Assert.That(deleted.JoinedObjects, Is.Empty);
            Assert.That(live.Exists, Is.True);
            Assert.That(live.MetaverseObjectTypePluralName, Is.EqualTo("People"), "what a link to it needs");
            Assert.That(live.JoinedObjects, Has.Count.EqualTo(1));
            var joined = live.JoinedObjects.Single();
            Assert.That(joined.ConnectedSystemObjectId, Is.EqualTo(seeded.CsoId));
            Assert.That(joined.ConnectedSystemId, Is.EqualTo(seeded.SystemId));
            Assert.That(joined.ConnectedSystemName, Is.EqualTo("Corporate Directory"));
            Assert.That(joined.TypeName, Is.EqualTo("user"));
            Assert.That(joined.ExternalId, Is.EqualTo("zbloggs"));
            Assert.That(joined.DisplayName, Is.EqualTo("Zoe Bloggs"), "named as the rest of the portal names it");
        }
    }

    [Test]
    public async Task GetMatchCandidatesAsync_ConnectedSystemObjectCandidates_ChecksEachStillExistsAsync()
    {
        var seeded = await SeedAsync();
        var deletedId = Guid.NewGuid();
        var itemId = await RecordAmbiguityAsync(
            Candidate(MatchCandidateKind.ConnectedSystemObject, seeded.CsoId, "zbloggs", ordinal: 0),
            Candidate(MatchCandidateKind.ConnectedSystemObject, deletedId, "zbloggs-old", ordinal: 1));

        var candidates = await NewJim().Activities.GetMatchCandidatesAsync(itemId);

        Assert.That(candidates.Select(c => c.Exists), Is.EqualTo(new[] { true, false }));
        Assert.That(candidates.All(c => c.JoinedObjects.Count == 0), Is.True, "only a Metaverse Object has objects joined to it");
    }

    [Test]
    public async Task GetMatchCandidatesAsync_ItemWithNoCandidates_ReadsNoneAsync()
    {
        var itemId = await RecordAmbiguityAsync();

        Assert.That(await NewJim().Activities.GetMatchCandidatesAsync(itemId), Is.Empty);
    }

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    private static ActivityRunProfileExecutionItemMatchCandidate Candidate(MatchCandidateKind kind, Guid id, string name, int ordinal) => new()
    {
        CandidateKind = kind,
        CandidateId = id,
        DisplayNameSnapshot = name,
        TypeNameSnapshot = "Person",
        MatchedValue = "48213",
        ObjectMatchingRuleDescriptionSnapshot = "Employee ID equals employeeNumber",
        Ordinal = ordinal,
        CandidateTotal = 2
    };

    private async Task<Guid> RecordAmbiguityAsync(params ActivityRunProfileExecutionItemMatchCandidate[] candidates)
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "Full Synchronisation",
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.CompleteWithError,
            InitiatedByType = ActivityInitiatorType.System
        };
        await using (var ctx = NewContext())
        {
            ctx.Activities.Add(activity);
            await ctx.SaveChangesAsync();
        }

        var item = new ActivityRunProfileExecutionItem
        {
            ActivityId = activity.Id,
            ErrorType = ActivityRunProfileExecutionItemErrorType.AmbiguousMatch,
            MatchCandidates = [.. candidates]
        };
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.BulkInsertRpeisAsync([item]);
        return item.Id;
    }

    private sealed record Seeded(int SystemId, Guid MvoId, Guid CsoId);

    /// <summary>A Metaverse Object with one Connected System Object joined to it.</summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Corporate Directory", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var externalIdAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "sAMAccountName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        csType.Attributes.Add(externalIdAttribute);
        var displayNameAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var departmentAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "department", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(displayNameAttribute);
        csType.Attributes.Add(departmentAttribute);
        var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow };
        seed.AddRange(connectorDefinition, system, csType, mvType, mvo);
        await seed.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            TypeId = csType.Id,
            ExternalIdAttributeId = externalIdAttribute.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            AttributeValues =
            [
                new ConnectedSystemObjectAttributeValue { AttributeId = externalIdAttribute.Id, StringValue = "zbloggs" },
                new ConnectedSystemObjectAttributeValue { AttributeId = displayNameAttribute.Id, StringValue = "Zoe Bloggs" },
                new ConnectedSystemObjectAttributeValue { AttributeId = departmentAttribute.Id, StringValue = "Finance" }
            ]
        };
        seed.ConnectedSystemObjects.Add(cso);
        await seed.SaveChangesAsync();
        return new Seeded(system.Id, mvo.Id, cso.Id);
    }
}
