// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace Tetron.PowerShellRemoting;

/// <summary>What kind of failure a <see cref="RemoteFailure"/> is, which decides how a caller reports it and whether to retry.</summary>
public enum RemoteFailureKind
{
    /// <summary>The command ran and reported an error: the object was not found, was already enabled, and so on.</summary>
    Command,

    /// <summary>The command was stopped before it finished.</summary>
    Stopped,

    /// <summary>
    /// Kerberos refused the account or the service (a wrong password, an unknown service principal, clock skew), or the
    /// server refused the authenticated account.
    /// </summary>
    Authentication,

    /// <summary>The endpoint's TLS certificate is not trusted, or does not carry the endpoint's name.</summary>
    Certificate,

    /// <summary>The endpoint refused, dropped or could not be reached. A retry may succeed.</summary>
    Connection,

    /// <summary>The endpoint did not answer within the request timeout. A retry may succeed.</summary>
    Timeout,

    /// <summary>Anything else the transport reported: a WS-Management fault, or a reply it could not read.</summary>
    Protocol
}
