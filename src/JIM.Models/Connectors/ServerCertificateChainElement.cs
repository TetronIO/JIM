// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Connectors;

/// <summary>
/// One certificate in a server's certificate chain, described for display.
/// </summary>
/// <remarks>
/// Public certificate material only, like the rest of <see cref="ServerCertificateDiagnostic"/>, which this is
/// serialised as part of.
/// </remarks>
public class ServerCertificateChainElement
{
    public string Subject { get; set; } = string.Empty;

    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// SHA-1 thumbprint, uppercase and unseparated.
    /// </summary>
    public string Thumbprint { get; set; } = string.Empty;

    public DateTime ValidFrom { get; set; }

    public DateTime ValidTo { get; set; }

    /// <summary>
    /// Whether the certificate says it is a certificate authority, allowed to issue other certificates.
    /// </summary>
    public bool IsCertificateAuthority { get; set; }

    /// <summary>
    /// Whether the certificate issued itself, which is what makes it a root.
    /// </summary>
    public bool IsSelfSigned { get; set; }

    public ServerCertificateChainElementSource Source { get; set; }

    /// <summary>
    /// The address it was downloaded from, where <see cref="Source"/> is
    /// <see cref="ServerCertificateChainElementSource.Downloaded"/>. Shown in full, because the address was named by a
    /// certificate nothing vouches for yet.
    /// </summary>
    public string? DownloadedFrom { get; set; }
}
