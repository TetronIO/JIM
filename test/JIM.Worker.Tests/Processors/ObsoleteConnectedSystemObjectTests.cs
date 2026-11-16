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
using JIM.Worker.Models;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// Deletion detection must be able to obsolete an object whatever type its anchor is.
///
/// <c>ProcessConnectedSystemObjectDeletionsAsync</c> works out which Connected System Objects are no
/// longer present and (Run Profile Safeguards, #1618 Layer 2's resolve-then-apply split) resolves each
/// missing anchor to its object by a lookup typed by the anchor's data type, then applies what it
/// found. An anchor type missing from that lookup once did not fail: it fell through to a null object,
/// logged "not found. No work to do." and returned, so the object was never obsoleted and the following
/// synchronisation never deleted it. Nothing was reported, and the run completed successfully.
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
    public async Task ProcessConnectedSystemObjectDeletions_LongAnchor_ObsoletesTheObjectAsync()
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
    public async Task ProcessConnectedSystemObjectDeletions_DecimalAnchor_ObsoletesTheObjectAsync()
    {
        var anchor = decimal.Parse("4200.00", CultureInfo.InvariantCulture);
        var result = await ObsoleteByAnchorAsync(anchor, av => av.DecimalValue = anchor);

        Assert.That(result.Updated, Has.Count.EqualTo(1));
        Assert.That(result.Updated[0].Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_IntAnchor_ObsoletesTheObjectAsync()
    {
        // A control: Number has always been dispatched, so this passing while the others fail is what
        // shows the fixture itself is sound and the defect is the missing arm rather than the harness.
        var result = await ObsoleteByAnchorAsync(Anchor, av => av.IntValue = Anchor);

        Assert.That(result.Updated, Has.Count.EqualTo(1));
        Assert.That(result.Updated[0].Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_ObjectNotYetObsolete_RecordsTheDeletionAsync()
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
    public async Task ProcessConnectedSystemObjectDeletions_ObjectAlreadyObsolete_ReportsAndChangesNothingAsync()
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
    public async Task ProcessConnectedSystemObjectDeletions_ObjectAlreadyObsolete_StillClearsStalePendingExportsAsync()
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
    public void ProcessConnectedSystemObjectDeletions_AnchorTypeItCannotLookUp_ThrowsRatherThanDoingNothing()
    {
        // The shape of the gap that let LongNumber through silently. An anchor type deletion detection cannot
        // look up by is a programming error, and deletion detection is the phase that decides whether an
        // object still exists, so it must fail loudly rather than report "no work to do".
        var anchor = new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc);

        Assert.That(
            async () => await ObsoleteByAnchorAsync(anchor, av => av.DateTimeValue = anchor),
            Throws.InstanceOf<ArgumentOutOfRangeException>().With.Message.Contains("DateTime"));
    }

    /// <summary>
    /// What one run of deletion detection did: the objects it queued for update,
    /// the execution items it recorded, and the repository it acted on (so a test can ask what became
    /// of the Pending Exports it seeded).
    /// </summary>
    private sealed record ObsoleteResult(
        List<ConnectedSystemObject> Updated,
        List<ActivityRunProfileExecutionItem> ExecutionItems,
        SyncRepository Repository);

    /// <summary>
    /// Seeds one Connected System Object carrying the given anchor, then invokes the production
    /// deletion detection with nothing imported, so that anchor is missing.
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

        // The Object Type's External ID is declared with the anchor's data type, which is what deletion detection
        // dispatches on.
        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Glitterband",
            ObjectTypes =
            [
                new ConnectedSystemObjectType
                {
                    Id = ObjectTypeId,
                    Name = "User",
                    ConnectedSystemId = ConnectedSystemId,
                    Selected = true,
                    Attributes =
                    [
                        new ConnectedSystemObjectTypeAttribute
                        {
                            Id = ExternalIdAttributeId,
                            Name = "anchor",
                            Type = DataTypeOf<T>(),
                            IsExternalId = true,
                            Selected = true
                        }
                    ]
                }
            ]
        };
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

        // Deletion detection resolves each missing anchor to its object, dispatching on the anchor's data type, then
        // applies what it found (Run Profile Safeguards, #1618 Layer 2). Invoking the production orchestrator with
        // nothing imported makes the seeded object missing, so both halves run exactly as a Full Import runs them.
        const string methodName = "ProcessConnectedSystemObjectDeletionsAsync";
        var method = typeof(SyncImportTaskProcessor).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{methodName} was not found. If it has been renamed or its signature " +
                "has changed, update this fixture to invoke the current production method; do not reimplement its " +
                "anchor-type dispatch or its obsoleting here, because those are the behaviour under test.");

        var toBeUpdated = new List<ConnectedSystemObject>();
        await (Task)method.Invoke(processor, [new ImportedExternalIds(), new List<ConnectedSystemObject>(), toBeUpdated, null])!;

        const string fieldName = "_activityRunProfileExecutionItems";
        var executionItems = (List<ActivityRunProfileExecutionItem>)(typeof(SyncImportTaskProcessor)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(processor)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{fieldName} was not found. It is where the import accumulates the " +
                "execution items it will persist, and whether an item lands there is the behaviour under test."));

        return new ObsoleteResult(toBeUpdated, executionItems, repository);
    }

    /// <summary>The External ID data type an anchor of this .NET type is declared with.</summary>
    private static AttributeDataType DataTypeOf<T>() => typeof(T) switch
    {
        var t when t == typeof(int) => AttributeDataType.Number,
        var t when t == typeof(long) => AttributeDataType.LongNumber,
        var t when t == typeof(decimal) => AttributeDataType.Decimal,
        var t when t == typeof(Guid) => AttributeDataType.Guid,
        var t when t == typeof(string) => AttributeDataType.Text,
        var t when t == typeof(DateTime) => AttributeDataType.DateTime,
        _ => throw new ArgumentOutOfRangeException(nameof(T), typeof(T).Name, "No External ID data type for this anchor type.")
    };
}
