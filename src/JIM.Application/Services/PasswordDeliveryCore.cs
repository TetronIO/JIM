// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// The one sequence every password JIM writes to a Connected System goes through (#1635): open the Connector's
/// password channel, refuse it if the system requires a secure transport and the channel is not encrypted, set
/// the password, classify a Connector that threw rather than answered, and close the channel.
/// <para>
/// Extracted once because it used to exist three times, in the queued delivery lane, the initial-password pass
/// and the immediate administrator set, and the three had drifted in what they logged and in whether a thrown
/// Connector was classified or allowed to escape. A Connected System now gets exactly one answer about its
/// channel however the password reached it, and a change to the rule is a change in one place.
/// </para>
/// <para>
/// Holds no state and no password. The cleartext value passes straight through to the Connector and is never
/// logged, never recorded and never returned; the caller owns how long it lives before and after the call.
/// </para>
/// </summary>
public static class PasswordDeliveryCore
{
    /// <summary>
    /// Opens the Connector's password channel for a Connected System and applies the system's transport rule to
    /// the channel that actually opened.
    /// <para>
    /// The result says one of three things: the channel is open and acceptable, and the caller must close it
    /// when it has finished; the channel could not be opened; or it opened and was refused as insecure, in which
    /// case it has already been closed here, because it was opened only to make the check. Nothing is sent in
    /// either failure case. Each failure carries a <see cref="PasswordSetResult"/> classifying it, so a caller
    /// that reports per account can hand that straight back, and a caller that reports per lane can read the
    /// flags.
    /// </para>
    /// </summary>
    public static PasswordChannelOpening OpenChannel(IConnectorPasswordManagement passwordConnector, ConnectedSystem connectedSystem)
    {
        ArgumentNullException.ThrowIfNull(passwordConnector);
        ArgumentNullException.ThrowIfNull(connectedSystem);

        try
        {
            passwordConnector.OpenPasswordConnection(connectedSystem.SettingValues);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A channel that could not be opened says nothing about whether the password would have been
            // acceptable, so it is transient rather than a rejection: reporting it as one would send an
            // administrator off to change a password that was never the problem.
            Log.Error(ex, "PasswordDeliveryCore: Could not open the password channel to Connected System {ConnectedSystemId}.", connectedSystem.Id);
            return PasswordChannelOpening.CouldNotOpen(ex.Message, PasswordSetResult.Failed(PasswordSetFailureReason.Transient,
                $"JIM could not open a password connection to the Connected System: {ex.Message}"));
        }

        // Asked once the channel exists, because what is being judged is the channel that opened rather than the
        // settings it was built from. A refused channel is a configuration fault rather than a transient one:
        // trying again changes nothing until somebody either encrypts the connection or accepts an unencrypted one.
        if (PasswordChannelSecurity.RefusesChannel(connectedSystem, passwordConnector))
        {
            passwordConnector.ClosePasswordConnection();
            Log.Error("PasswordDeliveryCore: Connected System {ConnectedSystemId} requires a secure transport for passwords and the Connector's password channel is not encrypted; no password was sent.",
                connectedSystem.Id);
            return PasswordChannelOpening.NotSecure(PasswordSetResult.Failed(PasswordSetFailureReason.ConfigurationFault,
                PasswordChannelSecurity.RefusalMessage(connectedSystem)));
        }

        return PasswordChannelOpening.Opened();
    }

    /// <summary>
    /// Sets one password over a channel already opened by <see cref="OpenChannel"/>, classifying a Connector that
    /// threw rather than answered as a transient failure: the throw says nothing about whether the password itself
    /// would be acceptable, and one target's fault must never stop a lane reaching the accounts behind it.
    /// Cancellation is the one exception that propagates, because an aborting caller must abort.
    /// </summary>
    public static async Task<PasswordSetResult> SetPasswordAsync(
        IConnectorPasswordManagement passwordConnector,
        ConnectedSystemObject target,
        string password,
        PasswordSetOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passwordConnector);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            return await passwordConnector.SetPasswordAsync(target, password, options, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "PasswordDeliveryCore: The Connector threw setting a password on Connected System Object {CsoId}.", target.Id);
            return PasswordSetResult.Failed(PasswordSetFailureReason.Transient, ex.Message);
        }
    }

    /// <summary>
    /// The whole sequence for one account: open, check, set, close. For a caller that has one password to write
    /// and no channel to keep; a caller writing several over one channel opens it once with
    /// <see cref="OpenChannel"/> and sets with <see cref="SetPasswordAsync"/>.
    /// </summary>
    public static async Task<PasswordSetResult> DeliverOnceAsync(
        IConnectorPasswordManagement passwordConnector,
        ConnectedSystem connectedSystem,
        ConnectedSystemObject target,
        string password,
        PasswordSetOptions options,
        CancellationToken cancellationToken)
    {
        var opening = OpenChannel(passwordConnector, connectedSystem);
        if (!opening.IsOpen)
            return opening.Failure!;

        try
        {
            return await SetPasswordAsync(passwordConnector, target, password, options, cancellationToken);
        }
        finally
        {
            passwordConnector.ClosePasswordConnection();
        }
    }
}

/// <summary>
/// What <see cref="PasswordDeliveryCore.OpenChannel"/> found. Exactly one of the three states holds.
/// </summary>
public sealed class PasswordChannelOpening
{
    private PasswordChannelOpening()
    {
    }

    /// <summary>
    /// The channel is open and acceptable. The caller owns closing it.
    /// </summary>
    public bool IsOpen { get; private init; }

    /// <summary>
    /// The Connector could not open its password channel. Nothing is open and nothing was sent.
    /// </summary>
    public bool CouldNotOpenChannel { get; private init; }

    /// <summary>
    /// The channel opened but the Connected System requires a secure transport and the channel is not encrypted.
    /// It has been closed again and nothing was sent.
    /// </summary>
    public bool ChannelNotSecure { get; private init; }

    /// <summary>
    /// The Connector's own words on why the channel could not be opened; null in the other two states.
    /// </summary>
    public string? OpenErrorMessage { get; private init; }

    /// <summary>
    /// The failure classified as a set-password result, for a caller reporting per account; null when the
    /// channel is open.
    /// </summary>
    public PasswordSetResult? Failure { get; private init; }

    internal static PasswordChannelOpening Opened() => new() { IsOpen = true };

    internal static PasswordChannelOpening CouldNotOpen(string errorMessage, PasswordSetResult failure) =>
        new() { CouldNotOpenChannel = true, OpenErrorMessage = errorMessage, Failure = failure };

    internal static PasswordChannelOpening NotSecure(PasswordSetResult failure) => new() { ChannelNotSecure = true, Failure = failure };
}
