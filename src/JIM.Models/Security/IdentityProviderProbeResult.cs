// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;
using JIM.Models.Core;

namespace JIM.Models.Security;

/// <summary>
/// What a connection to the identity provider's configuration document saw: the certificate it presented, how it was
/// judged, and whether the document came back.
/// </summary>
public class IdentityProviderProbeResult
{
    /// <summary>
    /// Whether a connection was made at all (a TLS handshake started, or a plain HTTP response came back).
    /// </summary>
    public bool Reached { get; init; }

    /// <summary>
    /// Whether the configuration document came back. False with <see cref="Reached"/> true means the identity provider
    /// answered but the handshake was refused, or it did not serve the document.
    /// </summary>
    public bool DocumentRead { get; init; }

    /// <summary>
    /// How the presented certificate was judged; null over plain HTTP or when nothing was reached.
    /// </summary>
    public CertificateStoreTrustDecision? Decision { get; init; }

    /// <summary>
    /// The identity provider's own certificate.
    /// </summary>
    public X509Certificate2? ServerCertificate { get; init; }

    /// <summary>
    /// The other certificates the identity provider sent.
    /// </summary>
    public IReadOnlyList<X509Certificate2> SentByServer { get; init; } = [];

    /// <summary>
    /// Why the identity provider could not be reached or its document read, where it could not.
    /// </summary>
    public string? Error { get; init; }
}
