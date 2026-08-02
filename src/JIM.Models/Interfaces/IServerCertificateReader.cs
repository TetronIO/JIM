// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Models.Interfaces;

/// <summary>
/// Looks at what a server presents over TLS, without connecting to it for any other purpose.
/// </summary>
/// <remarks>
/// An interface rather than a direct call to the probe so the application layer can be tested without standing up a
/// TLS server, and so the one place that opens a handshake to look at a certificate stays nameable.
/// </remarks>
public interface IServerCertificateReader
{
    /// <summary>
    /// Asks the server what certificate it presents, judging it against the supplied trust anchors.
    /// </summary>
    /// <param name="endpoint">Where to look, resolved from a Connected System's own settings.</param>
    /// <param name="trustedCertificates">Certificates from the JIM certificate store, treated as additional trust anchors.</param>
    /// <returns>What the server presented, or null when it could not be reached at all, which is a different problem.</returns>
    ServerCertificateReading? Read(SecureEndpoint endpoint, IReadOnlyCollection<X509Certificate2> trustedCertificates);
}
