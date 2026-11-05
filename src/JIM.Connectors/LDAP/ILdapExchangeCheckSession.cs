// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// An open Exchange session, as the LDAP Connector's settings check uses it (#2021): which commands the account can see,
/// and the version of the server it signed in to. Disposing it closes the session.
/// </summary>
internal interface ILdapExchangeCheckSession : IDisposable
{
    /// <summary>
    /// Which of the named commands the account can see. Exchange hides what the account's roles do not grant, so these
    /// are the ones it may run.
    /// </summary>
    Task<IReadOnlySet<string>> GetVisibleCommandsAsync(IReadOnlyList<string> names, CancellationToken cancellationToken);

    /// <summary>
    /// The version Exchange gives for the named server (its AdminDisplayVersion), or null when the account may not ask
    /// or the server does not say.
    /// </summary>
    Task<string?> GetServerVersionAsync(string server, CancellationToken cancellationToken);
}
