// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;

namespace JIM.Connectors.LDAP;

/// <summary>
/// An open Exchange session for the delivery lane (#2021): read a recipient, run a command, and say what the session has
/// cost. A command Exchange rejects throws <see cref="LdapExchangeCommandException"/>; cancellation throws as usual.
/// </summary>
internal interface ILdapExchangeRecipientSession : IDisposable
{
    /// <summary>What Exchange holds for <paramref name="target"/> now, read from the domain controller the export used.</summary>
    Task<LdapExchangeRecipient> ReadAsync(ExchangeRecipientTarget target, CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="command"/>.</summary>
    Task RunAsync(LdapExchangeCommand command, CancellationToken cancellationToken);

    /// <summary>What the session has cost so far (requirement 27).</summary>
    ExchangeSessionStatistics Statistics { get; }
}
