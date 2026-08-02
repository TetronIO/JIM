// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Connectors;

/// <summary>
/// What a server presented at the moment it was asked: its own certificate, and the authority that issued it where
/// the server sent one.
/// </summary>
/// <remarks>
/// Trusting the issuer is the durable choice, because it survives the server's certificate being renewed; trusting
/// the leaf works too, but has to be repeated at every renewal. A self-signed server sends only the leaf, which is
/// then the only thing there is to trust.
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
    /// The certificate that issued the leaf, where the server sent it. Null when the server sent only its own
    /// certificate, in which case there is no issuer to offer and the card should say so.
    /// </summary>
    public PresentedServerCertificate? Issuer { get; init; }

    public bool IsSelfSigned { get; init; }
}
