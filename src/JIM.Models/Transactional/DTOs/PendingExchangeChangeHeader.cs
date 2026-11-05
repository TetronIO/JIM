// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// One change on the Exchange queue, as the Operations Exchange tab, the REST API and PowerShell list it (#2021, Phase
/// 6b): what is declared for which account, where it stands, and how its last attempt failed.
/// </summary>
public class PendingExchangeChangeHeader
{
    /// <summary>The queued change.</summary>
    public Guid Id { get; set; }

    /// <summary>The change's own Activity, under which every attempt, retry and outcome is recorded.</summary>
    public Guid ActivityId { get; set; }

    /// <summary>The Connected System whose Exchange the change is for.</summary>
    public int ConnectedSystemId { get; set; }

    /// <inheritdoc cref="ConnectedSystemId"/>
    public string ConnectedSystemName { get; set; } = string.Empty;

    /// <summary>The account in that Connected System the change makes a recipient, or null once it has been deleted.</summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>The identity whose account it is, or null once the Metaverse Object has been deleted.</summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>The identity's display name, so a list can name a person rather than an id.</summary>
    public string? MetaverseObjectDisplayName { get; set; }

    /// <summary>The plural name of the identity's Metaverse Object Type, from which a link to it is built.</summary>
    public string? MetaverseObjectTypePluralName { get; set; }

    /// <summary>The kind of recipient declared, such as Mailbox, SharedMailbox, MailUser or None.</summary>
    public string RecipientType { get; set; } = string.Empty;

    /// <summary>Where the change stands in reaching Exchange.</summary>
    public PendingExchangeChangeStatus Status { get; set; }

    /// <summary>How the last attempt failed, or null for a change not attempted since it was queued or retried.</summary>
    public ExchangeChangeFailureKind? FailureKind { get; set; }

    /// <summary>Exchange's own words for the last failure, or JIM's reason for a refusal.</summary>
    public string? FailureMessage { get; set; }

    /// <summary>How many attempts have failed since the change was queued or last retried.</summary>
    public int AttemptCount { get; set; }

    /// <summary>When the change was queued.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When it was last attempted, or null if it has not been since it was queued or retried.</summary>
    public DateTime? LastAttemptedAt { get; set; }

    /// <summary>When the current run of failures began, from which JIM parks it after 24 hours.</summary>
    public DateTime? FirstFailedAt { get; set; }

    /// <summary>When the next attempt falls due, or null for a change due now or no longer attempted.</summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>The status of the change's own Activity, which tells a change JIM refused from one it does not make.</summary>
    public ActivityStatus ChangeActivityStatus { get; set; }

    /// <summary>Where the change stands, as its follow-up shows it: Queued, Delivering, Retrying, Parked, Refused or NotMade.</summary>
    public ExchangeChangeState State => ExchangeChangeOutcome.StateOf(Status, LastAttemptedAt, ChangeActivityStatus, held: !ConnectedSystemManagesExchangeRecipients);

    /// <summary>
    /// Whether the change's Connected System manages Exchange recipients now; while it does not, a waiting change is
    /// held.
    /// </summary>
    public bool ConnectedSystemManagesExchangeRecipients { get; init; } = true;

    /// <summary>
    /// Whether an administrator can retry it: it is parked, or retrying after an attempt. A change not yet attempted is
    /// already due, one being delivered is getting its attempt, and a refused one waits on its declared state changing.
    /// </summary>
    public bool CanRetry => PendingExchangeChange.IsRetryable(Status, LastAttemptedAt);
}
