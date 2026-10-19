// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;
using JIM.Models.Security;

namespace JIM.Application.Interfaces;

/// <summary>
/// Connects to the identity provider's OpenID Connect configuration document and reports what it presented. A seam
/// so checking and trusting can be tested without a TLS server.
/// </summary>
public interface IIdentityProviderProbe
{
    /// <param name="metadataAddress">The configuration document's address.</param>
    /// <param name="signInCertificates">The certificates the JIM certificate store trusts for sign-in.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<IdentityProviderProbeResult> ProbeAsync(Uri metadataAddress, IReadOnlyCollection<X509Certificate2> signInCertificates, CancellationToken cancellationToken);
}
