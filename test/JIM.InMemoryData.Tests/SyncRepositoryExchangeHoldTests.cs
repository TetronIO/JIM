// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// The in-memory twins of the two lookups the Exchange hold relies on (#2021) must answer exactly as the PostgreSQL ones
/// do, because the unit and workflow tests of the confirming import run over them: which objects have a change on the
/// Exchange queue in any status, and which hold a change awaiting Exchange with no queued change left to wait for.
/// </summary>
[TestFixture]
public class SyncRepositoryExchangeHoldTests
{
    private const int SystemId = 3;
    private const int OtherSystemId = 4;
    private SyncRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    [Test]
    public async Task GetConnectedSystemObjectIdsWithExchangeChangeAsync_QueuedChangesInEveryStatus_ReturnsTheAskedObjectsThatHaveOneAsync()
    {
        var statuses = Enum.GetValues<PendingExchangeChangeStatus>();
        var csoIds = Enumerable.Range(0, statuses.Length + 2).Select(_ => Guid.NewGuid()).ToList();
        var onAnotherSystem = Guid.NewGuid();
        await _repo.StageExchangeChangesAsync(statuses.Select((status, i) => Queued(SystemId, csoIds[i], status)).ToList());
        await _repo.StageExchangeChangesAsync([Queued(OtherSystemId, onAnotherSystem, PendingExchangeChangeStatus.Pending)]);
        var asked = csoIds.Skip(1).Append(onAnotherSystem).ToList();

        var found = await _repo.GetConnectedSystemObjectIdsWithExchangeChangeAsync(SystemId, asked);

        Assert.That(found, Is.EquivalentTo(csoIds.Skip(1).Take(statuses.Length - 1)));
    }

    [Test]
    public async Task GetConnectedSystemObjectIdsWithReleasedExchangeHoldsAsync_MixedObjects_ReturnsOnlyHoldsWithNoQueuedChangeAsync()
    {
        var stillQueued = await AddPendingExportAsync(SystemId, held: true);
        var nothingLeftToWaitFor = await AddPendingExportAsync(SystemId, held: true);
        var noLongerAwaitingConfirmation = await AddPendingExportAsync(SystemId, held: true, PendingExportAttributeChangeStatus.ExportedNotConfirmed);
        await AddPendingExportAsync(SystemId, held: false);
        await AddPendingExportAsync(OtherSystemId, held: true);
        await _repo.StageExchangeChangesAsync([Queued(SystemId, stillQueued, PendingExchangeChangeStatus.Cancelled)]);

        var released = await _repo.GetConnectedSystemObjectIdsWithReleasedExchangeHoldsAsync(SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.EquivalentTo(new[] { nothingLeftToWaitFor }));
            Assert.That(released, Does.Not.Contain(noLongerAwaitingConfirmation));
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectIdsAwaitingExchangeAsync_MixedObjects_ReturnsOnlyTheAskedObjectsWithAHeldChangeAsync()
    {
        var held = await AddPendingExportAsync(SystemId, held: true);
        var noLongerAwaitingConfirmation = await AddPendingExportAsync(SystemId, held: true, PendingExportAttributeChangeStatus.ExportedNotConfirmed);
        var unflagged = await AddPendingExportAsync(SystemId, held: false);
        var heldButNotAsked = await AddPendingExportAsync(SystemId, held: true);
        await _repo.StageExchangeChangesAsync([Queued(SystemId, held, PendingExchangeChangeStatus.Pending)]);

        var found = await _repo.GetConnectedSystemObjectIdsAwaitingExchangeAsync(SystemId, [held, noLongerAwaitingConfirmation, unflagged]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.EquivalentTo(new[] { held }), "whether or not anything is queued: the flag is the hold");
            Assert.That(found, Does.Not.Contain(heldButNotAsked));
        }
    }

    private async Task<Guid> AddPendingExportAsync(int systemId, bool held,
        PendingExportAttributeChangeStatus status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation)
    {
        var csoId = Guid.NewGuid();
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 77, Name = "Exchange Recipient Type", Type = AttributeDataType.Text, IsConnectorDefined = true
        };
        await _repo.CreatePendingExportsAsync(
        [
            new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = systemId,
                ConnectedSystemObjectId = csoId,
                ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Exported,
                AttributeValueChanges =
                [
                    new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(),
                        Attribute = attribute,
                        AttributeId = attribute.Id,
                        ChangeType = PendingExportAttributeChangeType.Update,
                        StringValue = "Mailbox",
                        Status = status,
                        AwaitsExchange = held
                    }
                ]
            }
        ]);
        return csoId;
    }

    private static PendingExchangeChange Queued(int systemId, Guid csoId, PendingExchangeChangeStatus status) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = systemId,
        ConnectedSystemObjectId = csoId,
        RecipientType = "Mailbox",
        Status = status,
        CreatedAt = DateTime.UtcNow,
        ActivityId = Guid.NewGuid()
    };
}
