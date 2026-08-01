// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Connectors;

/// <summary>
/// One look at what a server presents: the description an administrator is shown, and the certificates themselves.
/// </summary>
/// <remarks>
/// The two are produced from a single handshake so they cannot disagree with each other. Only the diagnostic is ever
/// shown or serialised; the chain exists so that an administrator who decides to trust what they were just shown can
/// have exactly that certificate added, rather than a copy taken at some earlier point.
/// </remarks>
public class ServerCertificateReading
{
    /// <summary>
    /// What the certificate is and which check it fails, judged with the JIM certificate store as trust anchors.
    /// </summary>
    public ServerCertificateDiagnostic Diagnostic { get; init; } = null!;

    /// <summary>
    /// The certificates the server sent. Null when it offered none, which the diagnostic reports as
    /// <see cref="ServerCertificateFailureReason.NoCertificatePresented"/>.
    /// </summary>
    public PresentedServerCertificateChain? Chain { get; init; }
}
