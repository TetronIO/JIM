// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Connectors;

/// <summary>
/// The certificate chain behind a server's certificate as JIM assembled it at the moment it was asked: the server's
/// own certificate, the certificate authorities between it and the root, and the root where JIM could reach one.
/// </summary>
/// <remarks>
/// JIM trusts a server when its chain ends at a root in the JIM certificate store. The certificate authorities in
/// between are not trusted themselves, but JIM needs every one of them to complete the chain, so the ones the server
/// did not send are stored alongside the root when it is trusted. A self-signed server certificate is its own root,
/// with nothing in between.
/// </remarks>
public class PresentedServerCertificateChain
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; }

    /// <summary>
    /// When the server was asked. Shown alongside the decision, because the whole point of reading again is that the
    /// administrator is trusting what is live rather than what was recorded earlier.
    /// </summary>
    public DateTime ReadAt { get; init; }

    /// <summary>
    /// The server's own certificate.
    /// </summary>
    public PresentedServerCertificate Leaf { get; init; } = null!;

    /// <summary>
    /// The certificate authorities between the server's certificate and the root, nearest the server first.
    /// </summary>
    public List<PresentedServerCertificate> Intermediates { get; init; } = [];

    /// <summary>
    /// The root at the top of the chain, where JIM reached one: the certificate to trust. The leaf itself for a
    /// self-signed server certificate. Null when the chain is incomplete.
    /// </summary>
    public PresentedServerCertificate? Root { get; init; }

    /// <summary>
    /// Certificates the administrator supplied that did not turn out to belong to this chain. Nothing is trusted
    /// while there are any, so a mistaken file is never stored.
    /// </summary>
    public List<PresentedServerCertificate> Unrelated { get; init; } = [];

    /// <summary>
    /// The subject of the first certificate JIM could not find, where the chain is incomplete.
    /// </summary>
    public string? MissingIssuer { get; init; }

    public bool IsComplete => Root != null;

    public bool IsSelfSigned { get; init; }
}
