// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Reflection;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.InMemoryData;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// Deletion detection must be able to obsolete an object whatever type its anchor is.
///
/// <c>ProcessConnectedSystemObjectDeletionsAsync</c> works out which Connected System Objects are no
/// longer present and hands each one's anchor to <c>ObsoleteConnectedSystemObjectAsync</c>, which
/// dispatches on the anchor's runtime type to fetch the object it must mark obsolete. An anchor type
/// missing from that dispatch does not fail: it falls through to a null object, logs
/// "not found. No work to do." and returns, so the object is never obsoleted and the following
/// synchronisation never deletes it. Nothing is reported, and the run completes successfully.
///
/// That was the state of the LongNumber anchor: the deletion-detection switch had handled it since it
/// was written and the repository overload existed, but the dispatch here did not name it. A Connected
/// System Object Type anchored on a 64-bit whole number therefore never had a deletion detected.
/// </summary>
[TestFixture]
public class ObsoleteConnectedSystemObjectTests
{
    private const int ConnectedSystemId = 1;
    private const int ExternalIdAttributeId = 10;
    private const int ObjectTypeId = 100;

    [Test]
    public async Task ObsoleteConnectedSystemObjectAsync_LongAnchor_ObsoletesTheObjectAsync()
    {
        // A bigint identity column, which is the ordinary shape of a large table's primary key.
        const long anchor = 9_007_199_254_740_993L;
        var updated = await ObsoleteByAnchorAsync(anchor, av => av.LongValue = anchor);

        Assert.That(updated, Has.Count.EqualTo(1),
            "A LongNumber-anchored object must be obsoleted. Falling through leaves it present forever, " +
            "with the run reporting success and the object reported as 'not found. No work to do.'");
        Assert.That(updated[0].Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
    }

    [Test]
    public async Task ObsoleteConnectedSystemObjectAsync_DecimalAnchor_ObsoletesTheObjectAsync()
    {
        var anchor = decimal.Parse("4200.00", CultureInfo.InvariantCulture);
        var updated = await ObsoleteByAnchorAsync(anchor, av => av.DecimalValue = anchor);

        Assert.That(updated, Has.Count.EqualTo(1));
        Assert.That(updated[0].Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
    }

    [Test]
    public async Task ObsoleteConnectedSystemObjectAsync_IntAnchor_ObsoletesTheObjectAsync()
    {
        // A control: Number has always been dispatched, so this passing while the others fail is what
        // shows the fixture itself is sound and the defect is the missing arm rather than the harness.
        const int anchor = 40711;
        var updated = await ObsoleteByAnchorAsync(anchor, av => av.IntValue = anchor);

        Assert.That(updated, Has.Count.EqualTo(1));
        Assert.That(updated[0].Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
    }

    [Test]
    public void ObsoleteConnectedSystemObjectAsync_AnchorTypeItCannotDispatch_ThrowsRatherThanDoingNothing()
    {
        // The arm that let LongNumber through silently. A type this method cannot fetch by is a
        // programming error in deletion detection, and deletion detection is the phase that decides
        // whether an object still exists, so it must fail loudly rather than report "no work to do".
        var anchor = new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc);

        Assert.That(
            async () => await ObsoleteByAnchorAsync(anchor, av => av.DateTimeValue = anchor),
            Throws.InstanceOf<ArgumentOutOfRangeException>().With.Message.Contains("DateTime"));
    }

    /// <summary>
    /// Seeds one Connected System Object carrying the given anchor, then invokes the production
    /// obsoleting method with that anchor, exactly as deletion detection does.
    /// </summary>
    /// <returns>The objects the method queued for update.</returns>
    private static async Task<List<ConnectedSystemObject>> ObsoleteByAnchorAsync<T>(
        T anchor,
        Action<ConnectedSystemObjectAttributeValue> populateAnchorColumn)
    {
        var repository = new SyncRepository();

        var anchorValue = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = ExternalIdAttributeId
        };
        populateAnchorColumn(anchorValue);

        repository.SeedConnectedSystemObject(new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            TypeId = ObjectTypeId,
            ExternalIdAttributeId = ExternalIdAttributeId,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow,
            AttributeValues = [anchorValue]
        });

        var connectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "Glitterband" };
        var runProfile = new ConnectedSystemRunProfile
        {
            Name = "Full Import",
            RunType = ConnectedSystemRunType.FullImport,
            ConnectedSystemId = ConnectedSystemId
        };
        var workerTask = TestUtilities.CreateTestWorkerTask(new Activity(), initiatedBy: null);
        using var cancellationTokenSource = new CancellationTokenSource();

        // The method under test reads the Connected System and the sync repository, both supplied for
        // real. The application and sync server are constructor dependencies of the wider import loop
        // and are not dereferenced on this path.
        var processor = new SyncImportTaskProcessor(
            null!,
            repository,
            null!,
            new SyncEngine(),
            new MockFileConnector(),
            connectedSystem,
            runProfile,
            workerTask,
            cancellationTokenSource);

        const string methodName = "ObsoleteConnectedSystemObjectAsync";
        var method = typeof(SyncImportTaskProcessor).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{methodName} was not found. If it has been renamed or its signature has " +
                "changed, update this fixture to invoke the current production method; do not reimplement its " +
                "anchor-type dispatch here, because that dispatch is the behaviour under test.");

        var toBeUpdated = new List<ConnectedSystemObject>();
        var task = (Task)method.MakeGenericMethod(typeof(T))
            .Invoke(processor, [anchor, ExternalIdAttributeId, toBeUpdated, new HashSet<Guid>()])!;
        await task;

        return toBeUpdated;
    }
}
