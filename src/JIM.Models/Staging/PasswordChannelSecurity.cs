// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;

namespace JIM.Models.Staging;

/// <summary>
/// Whether JIM may send a password down the channel a Connector has opened (#1119).
/// <para>
/// JIM writes a password to a Connected System on three occasions: the initial password on an account it
/// provisions, a password an administrator sets by hand, and a synchronised password change. All three ask this
/// same question at the same moment, immediately after the password channel opens and before anything is sent,
/// and all three must answer it on identical grounds. Stated once here rather than three times, because a rule
/// restated per path is a rule that drifts, and the drift means passwords leaving in the clear from whichever
/// path was forgotten.
/// </para>
/// <para>
/// What each path does about a refusal is its own business, and they differ properly: a queued password change
/// stays queued, accounts owed an initial password stay owed, and an administrator standing at a screen is told
/// outright. What none of them may do is send.
/// </para>
/// </summary>
public static class PasswordChannelSecurity
{
    /// <summary>
    /// Whether this Connected System's configuration forbids sending a password down this Connector's open
    /// password channel.
    /// </summary>
    /// <param name="connectedSystem">The system the password would be sent to.</param>
    /// <param name="passwordConnector">
    /// The Connector, with its password channel already open: <see cref="IConnectorPasswordManagement.IsPasswordChannelSecure"/>
    /// describes the channel that exists, so asking before opening it answers nothing.
    /// </param>
    public static bool RefusesChannel(ConnectedSystem connectedSystem, IConnectorPasswordManagement passwordConnector)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ArgumentNullException.ThrowIfNull(passwordConnector);

        return connectedSystem.RequireSecureTransport && !passwordConnector.IsPasswordChannelSecure;
    }

    /// <summary>
    /// What to tell an administrator when <see cref="RefusesChannel"/> refused. Names the system, so a reader of
    /// an Activity knows which one, and the setting by the name it carries in the portal, so they know where to
    /// go. Never contains a password or anything derived from one.
    /// </summary>
    public static string RefusalMessage(ConnectedSystem connectedSystem)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);

        return $"{connectedSystem.Name} has Require Secure Transport turned on, and JIM cannot confirm the " +
               "password connection is encrypted. No password was sent. Either enable an encrypted connection " +
               "in the Connected System's settings, or turn Require Secure Transport off if this system cannot " +
               "offer one.";
    }
}
