// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Connectors;

/// <summary>
/// Where JIM found a certificate in a server's certificate chain.
/// </summary>
/// <remarks>
/// Shown alongside each certificate so an administrator can judge it before trusting anything: a certificate the
/// server sent, or one downloaded from an address another certificate named, has not been vouched for by anyone yet.
/// </remarks>
public enum ServerCertificateChainElementSource
{
    /// <summary>
    /// The server sent it during the TLS handshake.
    /// </summary>
    SentByServer = 0,

    /// <summary>
    /// Downloaded from the address in the Authority Information Access extension of the certificate below it.
    /// </summary>
    Downloaded = 1,

    /// <summary>
    /// Already in the JIM certificate store.
    /// </summary>
    JimCertificateStore = 2,

    /// <summary>
    /// Already in the operating system's certificate stores on the machine JIM runs on.
    /// </summary>
    OperatingSystem = 3,

    /// <summary>
    /// Supplied by the administrator to complete a chain JIM could not complete itself.
    /// </summary>
    Supplied = 4
}
