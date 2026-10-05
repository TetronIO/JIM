// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Connectors;

/// <summary>
/// How a server expects the TLS handshake to reach it, which decides how JIM reads the certificate it presents.
/// </summary>
public enum SecureHandshakeFraming
{
    /// <summary>
    /// TLS from the first byte on the port, as LDAPS, HTTPS and Oracle TCPS listeners speak it.
    /// </summary>
    DirectTls = 0,

    /// <summary>
    /// Microsoft SQL Server's TDS 7.x framing: encryption is agreed in a PRELOGIN exchange, then the TLS handshake
    /// travels inside PRELOGIN packets. SQL Server drops a connection that opens with a bare TLS handshake instead.
    /// </summary>
    TdsPreLogin = 1
}
