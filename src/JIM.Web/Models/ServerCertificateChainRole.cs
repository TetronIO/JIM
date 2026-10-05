// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// The part a certificate plays in a server's certificate chain, which is what decides how long trusting it lasts.
/// </summary>
public enum ServerCertificateChainRole
{
    /// <summary>
    /// The server's own certificate. Trusting it lasts until it is renewed.
    /// </summary>
    Server,

    /// <summary>
    /// A certificate authority between the server's certificate and the root.
    /// </summary>
    Intermediate,

    /// <summary>
    /// The self-signed certificate authority at the top. A self-signed server certificate is its own root.
    /// </summary>
    Root
}
