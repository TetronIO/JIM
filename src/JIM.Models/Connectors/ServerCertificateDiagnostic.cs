// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Connectors;

/// <summary>
/// What a server presented when an encrypted connection to it was refused, and why it was refused.
/// </summary>
/// <remarks>
/// The platform LDAP client reports a rejected certificate the same way it reports an unreachable server, and hands
/// back nothing about the certificate itself. Everything here is gathered by connecting again over plain TLS purely
/// to look, so an administrator is told which certificate was presented and which check it failed, rather than being
/// left with "the server is unavailable" and no way to tell a certificate problem from a network one.
/// </remarks>
public class ServerCertificateDiagnostic
{
    /// <summary>
    /// The host the connection was made to, as configured on the Connected System. Compared against the certificate's
    /// subject and subject alternative names to decide <see cref="ServerCertificateFailureReason.NameMismatch"/>.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    /// <summary>
    /// Why the certificate was refused. Drives what an administrator is told to do about it.
    /// </summary>
    public ServerCertificateFailureReason FailureReason { get; set; }

    public string? Subject { get; set; }

    public string? Issuer { get; set; }

    /// <summary>
    /// The names the certificate was issued for, from its subject alternative name extension. The name check uses
    /// these, so showing them is what makes a mismatch self-explanatory.
    /// </summary>
    public List<string> SubjectAlternativeNames { get; set; } = [];

    public DateTime? ValidFrom { get; set; }

    public DateTime? ValidTo { get; set; }

    /// <summary>
    /// SHA-1 thumbprint, the identifier an administrator uses to confirm this is the same certificate they hold, and
    /// the one to search for in the JIM certificate store.
    /// </summary>
    public string? Thumbprint { get; set; }

    public string? SignatureAlgorithm { get; set; }

    /// <summary>
    /// Whether the certificate is self-signed, which tells an administrator whether to add the certificate itself to
    /// the JIM certificate store or the certificate authority that issued it.
    /// </summary>
    public bool IsSelfSigned { get; set; }

    /// <summary>
    /// SHA-1 thumbprint of the certificate that issued this one, where the server sent it alongside its own. Null
    /// where it did not, which is what makes the difference between offering an administrator the durable choice
    /// (trust the authority, and the decision survives renewal) and having only the leaf to offer.
    /// </summary>
    public string? IssuerThumbprint { get; set; }

    /// <summary>
    /// Whether the server sent the certificate authority that issued its own certificate, so it can be trusted
    /// directly. Self-signed certificates have no separate authority and so never do.
    /// </summary>
    public bool IsIssuerCertificateAvailable => !string.IsNullOrEmpty(IssuerThumbprint);

    /// <summary>
    /// A sentence naming what to do about it, shown alongside the certificate.
    /// </summary>
    public string? Remediation { get; set; }

    public bool IsExpired => ValidTo.HasValue && ValidTo.Value < DateTime.UtcNow;

    public bool IsNotYetValid => ValidFrom.HasValue && ValidFrom.Value > DateTime.UtcNow;
}
