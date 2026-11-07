// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Scale guard for #2813: optimistic export apply deletes the values an export superseded, and first detaches any
/// tracked copy of them. Matching every tracked value against a list of the removed ids was O(tracked x removed): in
/// the 500k-user leaver run a parallel export worker's batch tracked every member value of the AllStaff groups, and one
/// batch's apply took 6.8 s, almost all of it in that scan (900k tracked values against 9k removals measured 5,250 ms
/// with a list and 25 ms with a set).
/// </summary>
/// <remarks>
/// Stopwatch-bounded, as <c>OptimisticExportApplyCalculatorScaleTests</c> is: the bound is generous for the set-based
/// scan and far below what the quadratic one takes at this size, so the test fails loudly rather than flakily if the
/// quadratic scan comes back. The EF Core in-memory provider is used so the tracked instances are real change tracker
/// entries; the database round-trip itself is covered against PostgreSQL by <c>OptimisticExportApplyDatabaseTests</c>.
/// </remarks>
[TestFixture]
public class TrackedAttributeValueDetachScaleTests
{
    private const int TrackedValueCount = 300_000;
    private const int RemovedValueCount = 10_000;

    [Test]
    public async Task ApplyExportedAttributeValuesAsync_ManyTrackedValuesAndManyRemovals_DetachesAndDeletesWithinBoundAsync()
    {
        TestUtilities.SetEnvironmentVariables();
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        await using var context = new JimDbContext(options);
        using var repository = new PostgresDataRepository(context);

        // One group holding every value, as a large group's member list is: tracked Unchanged on the context, as a
        // tracked batch reload leaves them. Attached rather than saved, and with automatic change detection off, so the
        // in-memory provider's delete (the stand-in for the relational ExecuteDeleteAsync, which neither re-reads the
        // rows nor saves) finds nothing to fix up and does not rescan the tracker: the bound measures the detach.
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var values = new List<ConnectedSystemObjectAttributeValue>(TrackedValueCount);
        for (var i = 0; i < TrackedValueCount; i++)
        {
            values.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                ConnectedSystemObject = cso,
                AttributeId = 1,
                StringValue = $"cn=member{i},ou=people,dc=test,dc=local"
            });
        }
        cso.AttributeValues = new(values);
        context.Attach(cso);
        context.ChangeTracker.AutoDetectChangesEnabled = false;

        // Every thirtieth value removed, spread through the tracker rather than bunched at one end.
        var removalIds = values.Where((_, index) => index % (TrackedValueCount / RemovedValueCount) == 0)
            .Take(RemovedValueCount)
            .Select(value => value.Id)
            .ToList();
        Assert.That(removalIds, Has.Count.EqualTo(RemovedValueCount), "sanity check: the removal set is the intended size");

        var stopwatch = Stopwatch.StartNew();
        await repository.Sync.ApplyExportedAttributeValuesAsync([], removalIds, []);
        stopwatch.Stop();

        var removed = removalIds.ToHashSet();
        Assert.That(context.ConnectedSystemObjectAttributeValues.Local.Count(value => removed.Contains(value.Id)), Is.Zero,
            "every removed value must be gone from the tracker");
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)),
            $"Detaching and deleting {RemovedValueCount:N0} of {TrackedValueCount:N0} tracked values took " +
            $"{stopwatch.Elapsed.TotalMilliseconds:N0} ms; matching each tracked value against a list of the removed ids " +
            "is quadratic, against a set it is linear (#2813)");
    }
}
