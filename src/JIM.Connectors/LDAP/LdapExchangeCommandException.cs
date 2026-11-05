// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Tetron.PowerShellRemoting;

namespace JIM.Connectors.LDAP;

/// <summary>
/// An Exchange command that failed (#2021), with the failure as the remoting library classified it, for the delivery to
/// classify as one of the lane's failure kinds.
/// </summary>
internal sealed class LdapExchangeCommandException : Exception
{
    internal LdapExchangeCommandException(string command, RemoteFailure failure, Exception? inner = null)
        : base($"{command}: {failure.Message}", inner)
    {
        Command = command;
        Failure = failure;
    }

    /// <summary>The cmdlet that failed.</summary>
    internal string Command { get; }

    /// <summary>The failure, as the remoting library classified it.</summary>
    internal RemoteFailure Failure { get; }
}
