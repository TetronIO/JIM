// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// The in-memory twin of the Password Synchronisation queue filter must narrow exactly as the PostgreSQL one does,
/// because the portal and API tests run over it. What is pinned here is the one place the filter is wider than a
/// plain equality (#1635): asking for Pending, which the portal calls Waiting, also returns a change the Password
/// Delivery Service has claimed.
/// </summary>
[TestFixture]
public class SyncRepositoryPasswordChangeFilterTests
{
    private SyncRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    private static PendingPasswordChange Change(PendingPasswordChangeStatus status) => new()
    {
        Id = Guid.NewGuid(),
        MetaverseObjectId = Guid.NewGuid(),
        ConnectedSystemId = 3,
        ConnectedSystemObjectId = Guid.NewGuid(),
        EncryptedPassword = "$JIMPW$v1$ciphertext",
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(7),
        ActivityId = Guid.NewGuid(),
        Status = status
    };

    private Task<RangeResultSet<PendingPasswordChangeHeader>> ReadAsync(PendingPasswordChangeStatus? status) =>
        _repo.GetPendingPasswordChangeHeadersAsync(
            new PendingPasswordChangeFilter { Status = status }, 0, 10, "queued", sortDescending: false, includeTotalCount: true);

    [Test]
    public async Task GetPendingPasswordChangeHeadersAsync_PendingFilter_IncludesDeliveringRowsAsync()
    {
        await _repo.QueuePasswordChangesAsync(
        [
            Change(PendingPasswordChangeStatus.Pending),
            Change(PendingPasswordChangeStatus.Delivering),
            Change(PendingPasswordChangeStatus.Parked)
        ]);

        var window = await ReadAsync(PendingPasswordChangeStatus.Pending);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.TotalResults, Is.EqualTo(2));
            Assert.That(window.Results.Select(r => r.Status),
                Is.EquivalentTo(new[] { PendingPasswordChangeStatus.Pending, PendingPasswordChangeStatus.Delivering }));
        }
    }

    [Test]
    public async Task GetPendingPasswordChangeHeadersAsync_DeliveringFilter_IsStillExactAsync()
    {
        // The widening is one-way: Delivering on its own means exactly that.
        await _repo.QueuePasswordChangesAsync(
        [
            Change(PendingPasswordChangeStatus.Pending),
            Change(PendingPasswordChangeStatus.Delivering)
        ]);

        var window = await ReadAsync(PendingPasswordChangeStatus.Delivering);

        Assert.That(window.Results.Select(r => r.Status), Is.EqualTo(new[] { PendingPasswordChangeStatus.Delivering }));
    }
}
