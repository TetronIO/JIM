// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Connectors;

/// <summary>
/// One certificate a server presented during a TLS handshake, carrying the bytes needed to add it to the JIM
/// certificate store.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="ServerCertificateDiagnostic"/>, which describes a certificate for display
/// and is serialised onto Activities and API responses. This type carries the certificate itself, is only ever
/// produced at the moment an administrator decides to trust one, and is never persisted on a failure record nor
/// returned from the API.
/// </remarks>
public class PresentedServerCertificate
{
    /// <summary>
    /// SHA-1 thumbprint, uppercase and unseparated, as <see cref="System.Security.Cryptography.X509Certificates.X509Certificate2.Thumbprint"/> reports it.
    /// </summary>
    public string Thumbprint { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string Issuer { get; init; } = string.Empty;

    public DateTime ValidFrom { get; init; }

    public DateTime ValidTo { get; init; }

    /// <summary>
    /// The certificate itself, DER encoded. Public certificate material only; no private key is ever involved.
    /// </summary>
    public byte[] Data { get; init; } = [];

    /// <summary>
    /// The common name, which is what an administrator recognises the certificate by, and what JIM names it in the
    /// certificate store. Falls back to the whole subject where there is no common name to take.
    /// </summary>
    public string CommonName => CommonNameOf(Subject);

    /// <summary>
    /// Reads the common name out of a distinguished name such as "CN=dc01.corp.local, O=Corp".
    /// </summary>
    public static string CommonNameOf(string? distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
            return "Unknown";

        var commonName = distinguishedName
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(part => part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase));

        return commonName != null ? commonName[3..] : distinguishedName;
    }
}
