// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
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
}
