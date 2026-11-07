// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// One concept of "loaded in part" serves export execution (#2816) and the Delta Import (#2817), and one rule decides which
/// repository methods may save such an object: those writing only its row and the values and removals they are handed by
/// id. This runs a group a Delta Import loaded in part through the import's own save path
/// (<see cref="SyncServer.UpdateConnectedSystemObjectsAsync"/>) on real PostgreSQL: the save is accepted, the members the
/// import removed are gone, the member it added is held, and every member it never loaded is held exactly as it was.
/// Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent. <c>SetUp</c> TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class DeltaImportPartialLoadSaveDatabaseTests
{
    private static readonly MethodInfo UpdateMethod = typeof(SyncImportTaskProcessor).GetMethod(
        "UpdateConnectedSystemObjectFromImportObject", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("UpdateConnectedSystemObjectFromImportObject not found via reflection.");

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Delta Import partial-load save tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    [Test]
    public async Task UpdateConnectedSystemObjectsAsync_GroupADeltaImportLoadedInPart_SavesTheChangesAndKeepsEveryMemberItDidNotLoadAsync()
    {
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, userCount: 12, memberCount: 8);
        var before = await ReadMemberValuesAsync(seeded);

        // The directory dropped users 0 and 1 from the group and added user 9.
        var importObject = ImportObject([.. Enumerable.Range(2, 6).Select(LargeGroupImportSeed.UserDn), LargeGroupImportSeed.UserDn(9)]);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var groupType = (await repository.Sync.GetConnectedSystemObjectsByIdsAsync(seeded.SystemId, [seeded.GroupCsoId])).Single().Type;
        var questions = SyncImportTaskProcessor.HeldReferenceValueQuestionsFor(importObject, seeded.GroupCsoId, groupType);
        var answers = await repository.Sync.GetHeldReferenceValueAnswersAsync(questions);
        var answersByCsoId = answers.GroupBy(a => a.Question.ConnectedSystemObjectId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(a => a.Question.AttributeId));
        var group = (await SyncImportTaskProcessor.LoadConnectedSystemObjectsForComparisonAsync(repository.Sync, seeded.SystemId,
            [seeded.GroupCsoId], answersByCsoId)).Single();
        var externalIds = await SyncImportTaskProcessor.GetReferenceExternalIdsForComparisonAsync(repository.Sync, [group]);

        // What the object, loaded in part for the comparison, says it holds.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.PartiallyLoadedAttributes![seeded.MemberAttributeId], Is.TypeOf<ImportPartiallyLoadedAttribute>());
            Assert.That(group.AttributeValues.Count(v => v.AttributeId == seeded.MemberAttributeId), Is.EqualTo(2),
                "only the two members the database left undecided are loaded");
            Assert.That(group.HoldsAnyValue("member"), Is.True, "it holds members it did not load");
            Assert.That(() => group.HeldValueCount("member"), Throws.TypeOf<PartiallyLoadedObjectException>(),
                "how many members it holds is not known, and is not guessed from the two loaded");
            Assert.That(() => group.HeldValuesOf("member"), Throws.TypeOf<PartiallyLoadedObjectException>());
        }

        var executionItem = new ActivityRunProfileExecutionItem { ConnectedSystemObject = group };
        UpdateMethod.Invoke(null, [importObject, group, group.Type, executionItem, externalIds[seeded.GroupCsoId]]);
        Assert.That(group.PendingAttributeValueRemovals.Select(v => v.Id),
            Is.EquivalentTo(new[] { LargeGroupImportSeed.MemberValueId(0), LargeGroupImportSeed.MemberValueId(1) }));

        // The import's own save path.
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);
        await new SyncServer(jim).UpdateConnectedSystemObjectsAsync([group], [executionItem]);

        var after = await ReadMemberValuesAsync(seeded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.Where(v => v.Id != Guid.Empty && before.Any(b => b.Id == v.Id)),
                Is.EquivalentTo(before.Where(v => v.Id != LargeGroupImportSeed.MemberValueId(0) && v.Id != LargeGroupImportSeed.MemberValueId(1))),
                "every member it did not load (users 2 to 7) is held exactly as it was, and only users 0 and 1 are gone");
            Assert.That(after.Where(v => before.All(b => b.Id != v.Id)).Select(v => v.UnresolvedReferenceValue),
                Is.EqualTo(new[] { LargeGroupImportSeed.UserDn(9) }), "user 9 is added");
        }
    }

    /// <summary>
    /// Loading a group with its member attribute left out reads only the member rows it is asked for, never the rest of the
    /// membership. Every statement the load runs is captured and run again under EXPLAIN ANALYZE, and the rows read from the
    /// attribute values table (returned, or read and filtered out) are counted: a filter leaving the attribute out reads
    /// every member row of the group to discard it.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsByIdsAsync_LeavingTheMemberAttributeOut_ReadsOnlyTheMemberRowsAskedForAsync()
    {
        const int members = 20_000;
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, userCount: members, memberCount: members);
        Guid[] undecided = [LargeGroupImportSeed.MemberValueId(0), LargeGroupImportSeed.MemberValueId(1)];

        var recorder = new CommandRecordingInterceptor();
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(recorder)
            .Options;
        List<ConnectedSystemObject> loaded;
        await using (var ctx = new JimDbContext(options))
        {
            loaded = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsByIdsAsync(seeded.SystemId, [seeded.GroupCsoId],
                [seeded.MemberAttributeId], undecided);
        }

        var rowsRead = await PlanRowsRead.FromAsync(_connectionString, recorder.Commands, PlanRowsRead.AttributeValues);

        var group = loaded.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.AttributeValues.Where(v => v.AttributeId == seeded.MemberAttributeId).Select(v => v.Id), Is.EquivalentTo(undecided));
            Assert.That(group.AttributeValues.Count(v => v.AttributeId != seeded.MemberAttributeId), Is.EqualTo(2), "the group's DN and GUID");
            Assert.That(group.AttributeValues.All(v => v.Attribute != null && ReferenceEquals(v.ConnectedSystemObject, group)), Is.True);
            Assert.That(rowsRead, Is.LessThanOrEqualTo(10),
                $"the load read {rowsRead:N0} attribute value rows for a group of {members:N0} members; it needs the 2 undecided members and the group's 2 other values");
        }
    }

    private sealed record MemberValue(Guid Id, Guid? ReferenceValueId, string? UnresolvedReferenceValue);

    private async Task<List<MemberValue>> ReadMemberValuesAsync(LargeGroupImportSeed seeded)
    {
        await using var ctx = NewContext();
        return await ctx.ConnectedSystemObjectAttributeValues
            .AsNoTracking()
            .Where(v => EF.Property<Guid>(v, "ConnectedSystemObjectId") == seeded.GroupCsoId && v.AttributeId == seeded.MemberAttributeId)
            .Select(v => new MemberValue(v.Id, v.ReferenceValueId, v.UnresolvedReferenceValue))
            .ToListAsync();
    }

    private static ConnectedSystemImportObject ImportObject(List<string> members) => new()
    {
        ObjectType = "group",
        ChangeType = ObjectChangeType.Updated,
        Attributes =
        [
            new ConnectedSystemImportObjectAttribute { Name = "objectGUID", GuidValues = [LargeGroupImportSeed.GroupGuid] },
            new ConnectedSystemImportObjectAttribute { Name = "distinguishedName", StringValues = [LargeGroupImportSeed.GroupDn] },
            new ConnectedSystemImportObjectAttribute { Name = "member", ReferenceValues = members }
        ]
    };
}
