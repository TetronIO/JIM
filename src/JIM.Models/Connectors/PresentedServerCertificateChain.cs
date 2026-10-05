// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Connectors;

/// <summary>
/// The certificate chain behind a server's certificate as JIM assembled it at the moment it was asked: the server's
/// own certificate, the certificate authorities between it and the root, and the root where JIM could reach one.
/// </summary>
/// <remarks>
/// JIM trusts a server when any certificate in its chain is in the JIM certificate store, so any of them can be
/// chosen. The higher the choice, the longer it lasts: the root survives the renewal of everything below it, the
/// server's own certificate has to be trusted again at every renewal. A self-signed server certificate is its own
/// root, with nothing in between.
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
    /// The root at the top of the chain, where JIM reached one: the most durable certificate to trust. The leaf
    /// itself for a self-signed server certificate. Null when the chain is incomplete.
    /// </summary>
    public PresentedServerCertificate? Root { get; init; }

    /// <summary>
    /// The subject of the first certificate JIM could not find, where the chain is incomplete.
    /// </summary>
    public string? MissingIssuer { get; init; }

    public bool IsComplete => Root != null;

    /// <summary>
    /// Every certificate in the chain, the server's own first and the root, where there is one, last.
    /// </summary>
    public IEnumerable<PresentedServerCertificate> All =>
        Root == null || string.Equals(Root.Thumbprint, Leaf.Thumbprint, StringComparison.OrdinalIgnoreCase) ? [Leaf, .. Intermediates] : [Leaf, .. Intermediates, Root];

    public bool IsSelfSigned { get; init; }
}
