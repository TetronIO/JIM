// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Connectors;

/// <summary>
/// The result of trusting the certificate a Connected System's server presents.
/// </summary>
public class ServerCertificateTrustResult
{
    public ServerCertificateTrustOutcome Outcome { get; init; }

    /// <summary>
    /// The certificate as it now sits in the JIM certificate store. Set for
    /// <see cref="ServerCertificateTrustOutcome.Trusted"/> only.
    /// </summary>
    public TrustedCertificate? Certificate { get; init; }

    /// <summary>
    /// The thumbprint the administrator confirmed, and the one the server is presenting now. Both are set for
    /// <see cref="ServerCertificateTrustOutcome.ThumbprintMismatch"/>, so the two can be shown side by side rather
    /// than the administrator being told only that something changed.
    /// </summary>
    public string? ExpectedThumbprint { get; init; }

    public string? PresentedThumbprint { get; init; }

    /// <summary>
    /// The root of the server's chain, where JIM has it. Set for <see cref="ServerCertificateTrustOutcome.NotTheRoot"/>
    /// so the caller is told what to trust instead.
    /// </summary>
    public string? RootThumbprint { get; init; }

    public string? RootSubject { get; init; }

    /// <summary>
    /// The certificate JIM could not find. Set for <see cref="ServerCertificateTrustOutcome.ChainIncomplete"/>.
    /// </summary>
    public string? MissingIssuer { get; init; }

    /// <summary>
    /// Certificate authorities stored alongside the root so JIM can complete the chain: the ones the server did not
    /// send. Never trusted on their own. Set for <see cref="ServerCertificateTrustOutcome.Trusted"/>.
    /// </summary>
    public List<TrustedCertificate> StoredIntermediates { get; init; } = [];

    /// <summary>
    /// A sentence explaining the outcome, suitable for showing.
    /// </summary>
    public string? Message { get; init; }
}
