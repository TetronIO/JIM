// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;
namespace JIM.Models.Interfaces;

/// <summary>
/// Connectors that can set a password on an object in their Connected System implement this interface.
/// <para>
/// Passwords deliberately do not travel through Attribute Flow and Pending Exports. That pipeline persists
/// values, shows them in previews and change history, retries from persisted state, and expects a subsequent
/// import to confirm what was written; every one of those behaviours is wrong for a write-only secret that
/// cannot be read back. This is a separate channel for that reason.
/// </para>
/// </summary>
public interface IConnectorPasswordManagement
{
    /// <summary>
    /// The expiry behaviours this Connector is able to apply. Callers must offer only these to administrators.
    /// <para>
    /// A Connector that serves several kinds of target (as the LDAP Connector serves both Active Directory and
    /// generic directories) declares everything it can do somewhere, and reports a downgrade on the
    /// <see cref="PasswordSetResult"/> when the specific target in front of it cannot honour the request.
    /// </para>
    /// </summary>
    public IReadOnlyCollection<PasswordExpiryBehaviour> SupportedExpiryBehaviours { get; }

    /// <summary>
    /// Opens the connection used for password operations.
    /// <para>
    /// This is separate from the export connection because a password channel can have requirements an export
    /// channel does not. Active Directory, for instance, refuses to accept a password over an unencrypted
    /// connection, so a system whose exports run over plain LDAP still needs LDAPS to set passwords.
    /// </para>
    /// </summary>
    public void OpenPasswordConnection(IList<ConnectedSystemSettingValue> settings);

    /// <summary>
    /// Whether the password channel opened by <see cref="OpenPasswordConnection"/> encrypts what is sent over it.
    /// Undefined before that call and after <see cref="ClosePasswordConnection"/>.
    /// <para>
    /// A statement of fact about the channel, not a policy decision. Whether an unencrypted channel is acceptable
    /// belongs to the administrator, who declares it per Connected System with the Password Synchronisation
    /// "Require Secure Transport" setting; a Connector cannot know whether a given deployment is an isolated
    /// network with a directory that cannot serve TLS or a corporate one that simply has not been configured for
    /// it. Implementations report what they have and refuse nothing on this basis.
    /// </para>
    /// </summary>
    public bool IsPasswordChannelSecure { get; }

    /// <summary>
    /// Sets the password on a single object in the Connected System, applying the expiry behaviour and, where
    /// requested, enabling the account.
    /// </summary>
    /// <param name="target">The Connected System Object to set the password on.</param>
    /// <param name="password">The password to set. Never logged, never persisted, never returned.</param>
    /// <param name="options">How to apply the password, beyond the value itself.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>
    /// A classified result. Implementations must not throw for an ordinary rejection by the target; classify it
    /// as a <see cref="PasswordSetResult"/> instead, because the classification is what decides whether the work
    /// is retried or parked for an administrator.
    /// </returns>
    public Task<PasswordSetResult> SetPasswordAsync(ConnectedSystemObject target, string password, PasswordSetOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the connection used for password operations.
    /// </summary>
    public void ClosePasswordConnection();

    /// <summary>
    /// Checks whether the password channel is likely to work, without setting a password on anything.
    /// <para>
    /// There is no dry run for a password set: no directory offers a way to ask "would this be accepted" without
    /// really accepting it. What can be established without writing is everything around the password itself, and
    /// that accounts for most of what goes wrong: an unreachable target, an unencrypted channel, a mechanism the
    /// target does not offer, a service account without reset rights, an unreadable policy. This answers those.
    /// </para>
    /// <para>
    /// Implementations open and close their own connection, because this runs on demand from an administrator
    /// rather than inside an export session. They must not throw for a target that is unreachable or refuses a
    /// check: an unreachable target is a finding to report, not an exception to raise.
    /// </para>
    /// </summary>
    /// <param name="settings">The Connected System's settings, as for opening any other connection.</param>
    /// <param name="containerExternalIds">
    /// The external ids of the containers the Connected System manages, so that rights can be checked where JIM
    /// would actually be provisioning rather than somewhere it would not. Rights commonly vary between one part of
    /// a target and another, so a check against the wrong place answers the wrong question. May be empty, in which
    /// case the implementation falls back to whatever root the target exposes and says that it did so.
    /// </param>
    /// <param name="logger">Logger to write to.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public Task<PasswordPreflightResult> RunPasswordPreflightAsync(List<ConnectedSystemSettingValue> settings, IReadOnlyList<string> containerExternalIds, ILogger logger, CancellationToken cancellationToken);
}
