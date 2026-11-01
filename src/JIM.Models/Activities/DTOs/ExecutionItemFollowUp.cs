// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional.DTOs;

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// Work a Run Profile Execution Item set in motion that finishes after its run, such as the Initial Password of an
/// account an export provisioned, with where that work stands now.
/// </summary>
/// <remarks>
/// #2668. The follow-up is an Activity whose parent is the execution item. The run's own status is final, so a
/// follow-up that fails later is reported here and on its own Activity, never by changing the run.
/// </remarks>
public class ExecutionItemFollowUp
{
    /// <summary>
    /// The follow-up's Activity.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// The Run Profile Execution Item the follow-up belongs to.
    /// </summary>
    public Guid RunProfileExecutionItemId { get; set; }

    /// <summary>
    /// When the follow-up was recorded (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// What kind of work it is. PasswordSynchronisation is an account's Initial Password.
    /// </summary>
    public ActivityTargetType TargetType { get; set; }

    /// <summary>
    /// The Connected System the work is for.
    /// </summary>
    public string? TargetName { get; set; }

    /// <summary>
    /// The id of the Connected System the work is for, where there is one.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// How recording the follow-up went: completed with a warning when, for example, an Initial Password replaced a
    /// password change that was waiting for the same account.
    /// </summary>
    public ActivityStatus Status { get; set; }

    /// <summary>
    /// What was recorded when the follow-up began, such as "Initial password queued for delivery to PANOPLY.".
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// For an Initial Password, where it stands at its Connected System: queued, retrying, set, parked and so on,
    /// with its attempts. Null for any other kind of follow-up. A password carried by a change already waiting for
    /// the account has no targets of its own; that change's own Activity reports its delivery.
    /// </summary>
    public PasswordChangeOutcomes? PasswordOutcomes { get; set; }
}
