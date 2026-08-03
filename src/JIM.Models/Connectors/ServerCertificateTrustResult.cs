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
    /// A sentence explaining the outcome, suitable for showing.
    /// </summary>
    public string? Message { get; init; }
}
