// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;

namespace JIM.Models.Interfaces;

/// <summary>
/// Connectors that can search their Connected System for values already in use implement this interface (Unique
/// Value Generation, #242, release 3), and declare <see cref="IConnectorCapabilities.SupportsUniquenessProbe"/>.
/// The worker opens one probe connection per synchronisation run that generates a value, sends one batch per
/// object (that object's candidates plus a control value JIM already holds), and closes the connection when the run
/// ends.
/// </summary>
public interface IConnectorUniquenessProbe
{
    /// <summary>
    /// Opens the connection the probe searches over, with the Connected System's own settings, credentials and
    /// certificates, as the import and export paths do. Throws when the Connected System cannot be reached; the
    /// caller then checks against JIM's own records for the rest of the run.
    /// </summary>
    public void OpenUniquenessProbeConnection(ConnectedSystem connectedSystem, ILogger logger);

    /// <summary>
    /// Whether a system-wide search for <paramref name="attributeName"/> can say anything about uniqueness. An
    /// attribute unique only within its container (an LDAP naming attribute, say) cannot be probed this way: two
    /// entries in different containers holding the same value are not a collision.
    /// </summary>
    public bool CanProbeAttribute(string attributeName);

    /// <summary>
    /// Searches for one batch's candidates, returning one outcome per candidate in the request's order. Never throws
    /// for a fault in the Connected System: a refused, failed or timed-out search is a
    /// <see cref="UniquenessProbeResult.Failed"/> result that says why.
    /// </summary>
    public Task<UniquenessProbeResult> ProbeAsync(UniquenessProbeRequest request, ILogger logger, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the probe connection. Safe to call when it was never opened, or when opening it failed.
    /// </summary>
    public void CloseUniquenessProbeConnection();
}
