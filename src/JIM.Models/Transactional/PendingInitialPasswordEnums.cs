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
    /// Nothing will change until a person changes the configuration, so JIM has stopped trying.
    /// <para>
    /// The common case is a policy rejection: the target accepted the request and refused the password itself
    /// for not satisfying the rules in force for that account. The same generator configuration produces
    /// another password it refuses for the same reason, so retrying spends attempts reaching the same answer
    /// and buries the one thing that would fix it. A generator configuration that cannot be satisfied at all,
    /// and a target that cannot set a password on this kind of object, land here for the same reason.
    /// </para>
    /// <para>
    /// This is the distinction from <see cref="Pending"/>, and it is the whole point of having two states:
    /// pending means time or the environment may resolve it, parked means only an administrator can.
    /// Saving a changed configuration on the Synchronisation Rule releases everything parked against it.
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

/// <summary>
/// What came of trying to give a newly provisioned account its first password.
/// </summary>
public enum InitialPasswordDeliveryOutcome
{
    /// <summary>The password was set, and the account is no longer owed one.</summary>
    Delivered = 0,

    /// <summary>
    /// It did not work, but time or a corrected environment may resolve it: the directory was unreachable, or
    /// the account was not visible yet, which after a create is often nothing more than replication catching up.
    /// </summary>
    Retry = 1,

    /// <summary>
    /// It did not work and will not until somebody changes the configuration. See
    /// <see cref="PendingInitialPasswordStatus.Parked"/>.
    /// </summary>
    Parked = 2,

    /// <summary>
    /// There was nothing to do: the Synchronisation Rule does not ask for an initial password, or the Connected
    /// System cannot set one. Distinguished from a failure so that a run does not report work it never had.
    /// </summary>
    NotApplicable = 3
}
