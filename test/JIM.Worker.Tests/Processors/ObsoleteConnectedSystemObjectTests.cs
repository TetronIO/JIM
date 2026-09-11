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
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// Deletion detection must be able to obsolete an object whatever type its anchor is.
///
/// <c>ProcessConnectedSystemObjectDeletionsAsync</c> works out which Connected System Objects are no
/// longer present and, via <c>ResolveDeletionCandidateAsync</c> (Run Profile Safeguards, #1618 Layer
/// 2's resolve-then-apply split), hands each one's anchor to a lookup that dispatches on the anchor's
/// runtime type to fetch the object; <c>ApplyDeletionCandidateAsync</c> then does the marking. An
/// anchor type missing from the lookup's dispatch does not fail: it falls through to a null object,
/// logs "not found. No work to do." and returns, so the object is never obsoleted and the following
/// synchronisation never deletes it. Nothing is reported, and the run completes successfully.
///
/// That was the state of the LongNumber anchor: the deletion-detection switch had handled it since it
/// was written and the repository overload existed, but the dispatch here did not name it. A Connected
/// System Object Type anchored on a 64-bit whole number therefore never had a deletion detected.
///
/// The method must also say nothing the second time. An object stays Obsolete until a synchronisation
/// run on its own Connected System deletes it, so every import in between finds it missing again. Left
/// unguarded, each one re-flips a status that is already set and mints another
/// <c>ObjectChangeType.Deleted</c> execution item claiming the run deleted something, when the run did
/// nothing at all: the object was Obsolete before it started and Obsolete after. That is not merely
/// noise. The item inflates the Activity's deleted total, and the causality graph draws it as the start
/// of a fresh chain, because a deletion detected out of nowhere is exactly what it looks like.
/// </summary>
[TestFixture]
public class ObsoleteConnectedSystemObjectTests
{
    private const int ConnectedSystemId = 1;
    private const int ExternalIdAttributeId = 10;
    private const int ObjectTypeId = 100;
    private const int Anchor = 40711;

    [Test]
    public async Task ResolveAndApplyDeletionCandidateAsync_LongAnchor_ObsoletesTheObjectAsync()
    {
        // A bigint identity column, which is the ordinary shape of a large table's primary key.
        const long anchor = 9_007_199_254_740_993L;
        var result = await ObsoleteByAnchorAsync(anchor, av => av.LongValue = anchor);

        Assert.That(result.Updated, Has.Count.EqualTo(1),
            "A LongNumber-anchored object must be obsoleted. Falling through leaves it present forever, " +
            "with the run reporting success and the object reported as 'not found. No work to do.'");
        Assert.That(result.Updated[0].Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
    }

    [Test]
    public async Task ResolveAndApplyDeletionCandidateAsync_DecimalAnchor_ObsoletesTheObjectAsync()
    {
        var anchor = decimal.Parse("4200.00", CultureInfo.InvariantCulture);
        var result = await ObsoleteByAnchorAsync(anchor, av => av.DecimalValue = anchor);

        Assert.That(result.Updated, Has.Count.EqualTo(1));
        Assert.That(result.Updated[0].Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
    }

    [Test]
    public async Task ResolveAndApplyDeletionCandidateAsync_IntAnchor_ObsoletesTheObjectAsync()
    {
        // A control: Number has always been dispatched, so this passing while the others fail is what
        // shows the fixture itself is sound and the defect is the missing arm rather than the harness.
        var result = await ObsoleteByAnchorAsync(Anchor, av => av.IntValue = Anchor);

        Assert.That(result.Updated, Has.Count.EqualTo(1));
        Assert.That(result.Updated[0].Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
    }

    [Test]
    public async Task ResolveAndApplyDeletionCandidateAsync_ObjectNotYetObsolete_RecordsTheDeletionAsync()
    {
        // The first import to find the object missing is the one entitled to report it, and the pair of
        // tests below only mean anything against this: they assert absence, so without a control showing
        // the same harness producing an item, they would pass just as well on a broken fixture.
        var result = await ObsoleteByAnchorAsync(Anchor, av => av.IntValue = Anchor);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ExecutionItems, Has.Count.EqualTo(1),
                "The first detection must report: it is the only record that the object went missing.");
            Assert.That(result.ExecutionItems[0].ObjectChangeType, Is.EqualTo(ObjectChangeType.Deleted));
            Assert.That(result.Updated, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task ResolveAndApplyDeletionCandidateAsync_ObjectAlreadyObsolete_ReportsAndChangesNothingAsync()
    {
        var result = await ObsoleteByAnchorAsync(Anchor, av => av.IntValue = Anchor,
            status: ConnectedSystemObjectStatus.Obsolete);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ExecutionItems, Is.Empty,
                "An object that was already Obsolete was not deleted by this run: it was Obsolete before the " +
                "run started. An execution item here claims a deletion that did not happen, inflates the " +
                "Activity's deleted total, and gives the causality graph a chain start with no cause.");
            Assert.That(result.Updated, Is.Empty,
                "Re-writing a status that already holds the target value is a database write for no change.");
        }
    }

    [Test]
    public async Task ResolveAndApplyDeletionCandidateAsync_ObjectAlreadyObsolete_StillClearsStalePendingExportsAsync()
    {
        // Export evaluation does not exclude Obsolete objects, so a synchronisation on a *different*
        // Connected System can stage an export against one between imports. Clearing those is the one
        // piece of work a repeat detection genuinely has to do: the export would otherwise be sent to a
        // target object that no longer exists. Reporting nothing must not become doing nothing.
        var result = await ObsoleteByAnchorAsync(Anchor, av => av.IntValue = Anchor,
            status: ConnectedSystemObjectStatus.Obsolete, seedPendingExport: true);

        var remaining = await result.Repository.GetPendingExportsCountAsync(ConnectedSystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(remaining, Is.Zero, "A Pending Export staged against a deleted target object is stale.");
            Assert.That(result.ExecutionItems, Is.Empty);
        }
    }

    [Test]
    public void ResolveDeletionCandidateAsync_AnchorTypeItCannotDispatch_ThrowsRatherThanDoingNothing()
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
    /// What one invocation of the production obsoleting method did: the objects it queued for update,
    /// the execution items it recorded, and the repository it acted on (so a test can ask what became
    /// of the Pending Exports it seeded).
    /// </summary>
    private sealed record ObsoleteResult(
        List<ConnectedSystemObject> Updated,
        List<ActivityRunProfileExecutionItem> ExecutionItems,
        SyncRepository Repository);

    /// <summary>
    /// Seeds one Connected System Object carrying the given anchor, then invokes the production
    /// obsoleting method with that anchor, exactly as deletion detection does.
    /// </summary>
    /// <param name="anchor">The External ID value deletion detection found missing from the import.</param>
    /// <param name="populateAnchorColumn">Writes the anchor into the typed column the repository looks it up by.</param>
    /// <param name="status">The status the object already carries when the method is called.</param>
    /// <param name="seedPendingExport">Whether to stage a Pending Export against the object first.</param>
    private static async Task<ObsoleteResult> ObsoleteByAnchorAsync<T>(
        T anchor,
        Action<ConnectedSystemObjectAttributeValue> populateAnchorColumn,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal,
        bool seedPendingExport = false)
    {
        var repository = new SyncRepository();

        var anchorValue = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = ExternalIdAttributeId
        };
        populateAnchorColumn(anchorValue);

        var csoId = Guid.NewGuid();
        repository.SeedConnectedSystemObject(new ConnectedSystemObject
        {
            Id = csoId,
            ConnectedSystemId = ConnectedSystemId,
            TypeId = ObjectTypeId,
            ExternalIdAttributeId = ExternalIdAttributeId,
            Status = status,
            Created = DateTime.UtcNow,
            AttributeValues = [anchorValue]
        });

        if (seedPendingExport)
            repository.SeedPendingExport(new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = ConnectedSystemId,
                ConnectedSystemObjectId = csoId,
                ChangeType = PendingExportChangeType.Update
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

        // Run Profile Safeguards (#1618, Layer 2) split deletion detection into a resolve phase (no
        // side effects; dispatches on the anchor's runtime type) and an apply phase (acts on an
        // already-resolved candidate). Invoking both in sequence, exactly as the production
        // orchestrator does once it has decided the run's limits are not exceeded, exercises the same
        // combined behaviour the single method used to.
        const string resolveMethodName = "ResolveDeletionCandidateAsync";
        var resolveMethod = typeof(SyncImportTaskProcessor).GetMethod(resolveMethodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{resolveMethodName} was not found. If it has been renamed or its signature " +
                "has changed, update this fixture to invoke the current production method; do not reimplement its " +
                "anchor-type dispatch here, because that dispatch is the behaviour under test.");

        const string applyMethodName = "ApplyDeletionCandidateAsync";
        var applyMethod = typeof(SyncImportTaskProcessor).GetMethod(applyMethodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{applyMethodName} was not found. If it has been renamed or its signature " +
                "has changed, update this fixture to invoke the current production method; do not reimplement its " +
                "obsoleting behaviour here, because that behaviour is what these tests pin.");

        var toBeUpdated = new List<ConnectedSystemObject>();

        var resolveTask = (Task)resolveMethod.MakeGenericMethod(typeof(T))
            .Invoke(processor, [anchor, ExternalIdAttributeId, new HashSet<Guid>()])!;
        await resolveTask;
        var cso = (ConnectedSystemObject?)resolveTask.GetType().GetProperty("Result")!.GetValue(resolveTask);

        if (cso != null)
        {
            var applyTask = (Task)applyMethod.Invoke(processor, [cso, toBeUpdated])!;
            await applyTask;
        }

        const string fieldName = "_activityRunProfileExecutionItems";
        var executionItems = (List<ActivityRunProfileExecutionItem>)(typeof(SyncImportTaskProcessor)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(processor)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{fieldName} was not found. It is where the import accumulates the " +
                "execution items it will persist, and whether an item lands there is the behaviour under test."));

        return new ObsoleteResult(toBeUpdated, executionItems, repository);
    }
}
