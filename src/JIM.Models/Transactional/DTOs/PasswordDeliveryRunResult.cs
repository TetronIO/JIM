// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// What one delivery pass over a Connected System's queued password changes achieved (#1119).
/// <para>
/// Reported rather than thrown, for the same reason the initial-password pass reports: a delivery that did not
/// work is an outcome to record against the change it belongs to, not a failure of the pass. A pass that threw
/// would abandon the changes it had not reached and lose the outcomes of the ones it had.
/// </para>
/// </summary>
public class PasswordDeliveryRunResult
{
    /// <summary>Changes delivered and removed from the queue.</summary>
    public int DeliveredCount { get; set; }

    /// <summary>Changes that failed in a way another attempt may resolve, and are scheduled to be retried.</summary>
    public int RetryingCount { get; set; }

    /// <summary>
    /// Changes JIM has stopped trying: the target refused the password, the operation is unsupported, or the
    /// configured attempts ran out. Each stays visible and manually retryable.
    /// </summary>
    public int ParkedCount { get; set; }

    /// <summary>
    /// Changes that outlived their time to live and were expired rather than attempted, before this pass began.
    /// </summary>
    public int ExpiredCount { get; set; }

    /// <summary>
    /// True where the Connector could not open its password channel at all, so nothing was attempted. Reported
    /// once for the pass rather than as a failure per change, which would inflate every attempt count for a
    /// problem that belongs to the connection.
    /// </summary>
    public bool CouldNotOpenPasswordConnection { get; set; }

    /// <summary>
    /// True where this Connected System's Connector cannot set passwords at all. The queued changes are left
    /// exactly as they are: the capability may arrive with a Connector upgrade.
    /// </summary>
    public bool ConnectorCannotSetPasswords { get; set; }

    /// <summary>
    /// True where this Connected System requires a secure transport for passwords and the Connector's password
    /// channel is not encrypted. Nothing was sent and no attempt was counted; the queued changes wait for a
    /// secure channel rather than being delivered against the administrator's explicit instruction.
    /// </summary>
    public bool PasswordChannelNotSecure { get; set; }

    /// <summary>
    /// Whether this pass has anything worth telling an administrator about.
    /// </summary>
    public bool HasSomethingToReport =>
        DeliveredCount > 0 || RetryingCount > 0 || ParkedCount > 0 || ExpiredCount > 0
        || CouldNotOpenPasswordConnection || ConnectorCannotSetPasswords || PasswordChannelNotSecure;
}
