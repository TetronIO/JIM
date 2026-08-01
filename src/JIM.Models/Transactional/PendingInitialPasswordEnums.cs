// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// Where an account stands in getting the initial password its Synchronisation Rule asked for.
/// <para>
/// There is deliberately no "delivered" state. A successful delivery removes the record, because this is a list
/// of work outstanding rather than a history of work done; the Activity is the history. Keeping delivered rows
/// would grow a table by one row per account ever provisioned, to answer a question Activities already answer.
/// </para>
/// </summary>
public enum PendingInitialPasswordStatus
{
    /// <summary>
    /// The account is owed a password and JIM will try again. Covers a first attempt not yet made, and one that
    /// failed for a reason retrying can resolve: the directory was unreachable, or its configuration has to be
    /// corrected before it will accept the write.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// The target accepted the request and refused the password itself, because it does not satisfy the policy
    /// in force for that account.
    /// <para>
    /// Deliberately not retried. The same generator configuration produces another password the target will
    /// refuse for the same reason, so retrying spends attempts to reach the same answer, and buries the one
    /// thing that would fix it: an administrator changing the configuration. Saving a changed configuration on
    /// the Synchronisation Rule releases everything parked against it.
    /// </para>
    /// </summary>
    Parked = 1,

    /// <summary>
    /// Long enough passed without success that JIM stopped trying.
    /// <para>
    /// Recorded rather than removed. An account that quietly stopped being owed a password, with nothing to say
    /// so, is exactly the kind of silent loss the rest of this feature is built to avoid.
    /// </para>
    /// </summary>
    Expired = 2
}
