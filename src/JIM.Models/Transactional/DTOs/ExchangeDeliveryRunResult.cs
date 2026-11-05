// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// What one Exchange delivery lane did on one Connected System (#2021): its outcome counts, whether it could deliver
/// at all, and what its Exchange session cost. Logged as the lane's summary (PRD requirement 27).
/// </summary>
public class ExchangeDeliveryRunResult
{
    /// <summary>The Connected System's name, once the lane has loaded it.</summary>
    public string? ConnectedSystemName { get; set; }

    /// <summary>Changes Exchange now has, and removed from the queue.</summary>
    public int DeliveredCount { get; set; }

    /// <summary>Of the delivered changes, those whose recipient already had its declared state.</summary>
    public int AlreadyInStateCount { get; set; }

    /// <summary>Changes that failed and will be tried again.</summary>
    public int RetryingCount { get; set; }

    /// <summary>Changes that failed and wait for a person.</summary>
    public int ParkedCount { get; set; }

    /// <summary>Changes withdrawn because nobody is owed them any more, and removed from the queue.</summary>
    public int WithdrawnCount { get; set; }

    /// <summary>Changes claimed and given back unattempted.</summary>
    public int ReleasedCount { get; set; }

    /// <summary>The Connector cannot manage Exchange recipients, so the lane gave back everything it claimed.</summary>
    public bool ConnectorCannotManageExchangeRecipients { get; set; }

    /// <summary>
    /// True when the Connected System has Exchange recipient management turned off: its claimed changes were given back
    /// unattempted, to wait until it is turned on again.
    /// </summary>
    public bool NotManagingExchangeRecipients { get; set; }

    /// <summary>Why the Exchange session could not be opened, or null where it opened (or was never needed).</summary>
    public string? SessionFailure { get; set; }

    /// <summary>What the lane's Exchange session cost.</summary>
    public ExchangeSessionStatistics SessionStatistics { get; set; } = ExchangeSessionStatistics.None;

    /// <summary>
    /// Whether the lane could not deliver at all. The scheduler then holds the system off for a poll rather than
    /// running the same failing lane back to back.
    /// </summary>
    public bool CouldNotDeliver => ConnectorCannotManageExchangeRecipients || NotManagingExchangeRecipients || SessionFailure != null;

    /// <summary>Whether the lane found anything to do or to report.</summary>
    public bool HasSomethingToReport =>
        DeliveredCount + RetryingCount + ParkedCount + WithdrawnCount + ReleasedCount > 0 || CouldNotDeliver;
}
