// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional.DTOs;

namespace JIM.Web.Services;

/// <summary>
/// Waits on a queued password change until every target has settled or a timeout runs out (#1635). What the
/// Set Password dialog's result stage and the REST endpoint's <c>wait</c> parameter are built on.
/// </summary>
public interface IPasswordChangeOutcomeWaiter
{
    /// <summary>
    /// Returns the change's per-target outcomes as soon as every target is settled (nothing Queued or Delivering),
    /// or the latest outcomes when <paramref name="timeout"/> elapses first. Null only where no change with that
    /// Activity id exists.
    /// </summary>
    /// <param name="activityId">The change's Activity id, as returned when it was queued.</param>
    /// <param name="timeout">How long to wait for the change to settle before answering with what is known.</param>
    /// <param name="cancellationToken">Abandons the wait; an <see cref="OperationCanceledException"/> is thrown rather than an answer returned.</param>
    Task<PasswordChangeOutcomes?> WaitForOutcomesAsync(Guid activityId, TimeSpan timeout, CancellationToken cancellationToken);
}
