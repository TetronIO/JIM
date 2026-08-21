// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Tasking;

/// <summary>
/// Worker task that delivers the password changes due in the Password Synchronisation queue (#1119).
/// <para>
/// Its own task type rather than a step on an export run, which is where initial passwords are delivered. That
/// difference is deliberate: an initial password is owed to an account an export has just created, so the export
/// run is exactly when to try. A synchronised password is owed because somebody changed their password, which
/// has nothing to do with any run profile, and waiting for the next export would leave a person with different
/// passwords across their systems until one happened to be scheduled.
/// </para>
/// </summary>
public class PasswordDeliveryWorkerTask : WorkerTask
{
    public PasswordDeliveryWorkerTask()
    {
        // for use by EntityFramework to construct db-sourced objects.
    }

    /// <summary>
    /// The Connected System to deliver to, or null to deliver to every system with work due.
    /// <para>
    /// Named where the trigger knows which system it is (a change queued for one target, or one just enabled),
    /// so the pass does not sweep systems with nothing to do. Null where the trigger is the clock: housekeeping
    /// noticing that retries have fallen due does not know in advance which systems they belong to.
    /// </para>
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// Raised by JIM itself: a password change fanning out, a Connected System being enabled, or housekeeping
    /// finding retries due.
    /// </summary>
    public static PasswordDeliveryWorkerTask ForSystem(string initiatedByName, int? connectedSystemId = null)
    {
        return new PasswordDeliveryWorkerTask
        {
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = initiatedByName,
            ConnectedSystemId = connectedSystemId
        };
    }

    /// <summary>
    /// Raised by an administrator retrying queued work from the portal.
    /// </summary>
    public static PasswordDeliveryWorkerTask ForUser(Guid initiatedById, string initiatedByName, int? connectedSystemId = null)
    {
        return new PasswordDeliveryWorkerTask
        {
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = initiatedById,
            InitiatedByName = initiatedByName,
            ConnectedSystemId = connectedSystemId
        };
    }
}
