// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;
using JIM.Models.Staging;

namespace JIM.Models.Interfaces;

/// <summary>
/// Connectors whose Connected System's accounts can be made Exchange recipients implement this interface (#2021).
/// The Exchange delivery lane discovers it on the Connector and drives one session per Connected System through it:
/// open, converge each queued change in turn, close.
/// <para>
/// Exchange is a second system beside the directory, with its own availability and failure modes, so it is reached
/// through this separate channel rather than through Attribute Flow and the export: an export writes the account and
/// leaves the recipient it should be on the Exchange queue, and the lane applies it on its own clock.
/// </para>
/// </summary>
public interface IConnectorExchangeRecipients
{
    /// <summary>
    /// Whether the Connected System these settings belong to manages Exchange recipients now. While it does not, the
    /// lane leaves its queued changes waiting, unattempted, and delivers them once it does again: turning management off
    /// holds delivery rather than failing it.
    /// </summary>
    bool IsManagingExchangeRecipients(IList<ConnectedSystemSettingValue> settings);

    /// <summary>
    /// Opens the session the lane's changes are converged over, signed in with the Connected System's own
    /// credentials.
    /// </summary>
    /// <exception cref="ExchangeSessionException">The session could not be opened: Exchange is unreachable, or the
    /// sign-in failed. The lane records the failure against each change it claimed, so it is shown against the
    /// person, and retries with backoff.</exception>
    Task OpenExchangeSessionAsync(IList<ConnectedSystemSettingValue> settings, CancellationToken cancellationToken);

    /// <summary>
    /// Brings <paramref name="target"/> to <paramref name="declared"/>: reads the recipient's current state first and
    /// does only what converges it (PRD requirement 9), so converging a recipient already in its declared state
    /// changes nothing.
    /// </summary>
    /// <returns>Delivered, or how it failed. A refusal is a result, never an exception, and its message carries
    /// nothing secret.</returns>
    Task<ExchangeConvergeResult> ConvergeAsync(ExchangeRecipientTarget target, ExchangeDeclaredState declared, CancellationToken cancellationToken);

    /// <summary>
    /// What the open session has cost so far, for the lane's summary (requirement 27).
    /// </summary>
    ExchangeSessionStatistics GetSessionStatistics();

    /// <summary>
    /// Closes the session. Safe to call when no session is open.
    /// </summary>
    void CloseExchangeSession();
}
