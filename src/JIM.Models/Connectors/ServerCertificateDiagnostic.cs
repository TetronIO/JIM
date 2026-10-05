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
    /// SHA-1 thumbprint of the certificate authority directly above this certificate, where JIM has it. Null for a
    /// self-signed certificate, and where JIM could not find the issuer. Any certificate in <see cref="Chain"/> can be
    /// trusted; this one and <see cref="RootThumbprint"/> are named because they are the ones usually wanted.
    /// </summary>
    public string? IssuerThumbprint { get; set; }

    /// <summary>
    /// Whether JIM has the certificate authority that issued this certificate, so it can be trusted directly.
    /// Self-signed certificates have no separate authority and so never do. Kept for scripts written before
    /// <see cref="Chain"/> described every certificate.
    /// </summary>
    public bool IsIssuerCertificateAvailable => !string.IsNullOrEmpty(IssuerThumbprint);

    /// <summary>
    /// The certificate chain JIM assembled, starting with the server's certificate and ending with the root where
    /// JIM could reach one. Built from what the server sent, what JIM could download from the addresses certificates
    /// name, and what JIM already holds. Empty on diagnostics recorded before JIM described chains.
    /// </summary>
    public List<ServerCertificateChainElement> Chain { get; set; } = [];

    /// <summary>
    /// Whether the chain reaches a root (a self-signed certificate authority). When it does not, the certificates JIM
    /// has can still be trusted; the root just is not among them.
    /// </summary>
    public bool IsChainComplete { get; set; }

    /// <summary>
    /// The subject of the first certificate JIM could not find, where the chain is incomplete: the issuer of the
    /// highest certificate JIM has. What an administrator asks their PKI team for.
    /// </summary>
    public string? MissingIssuer { get; set; }

    /// <summary>
    /// The root at the top of a complete chain: the most durable certificate to trust, since it survives the renewal
    /// of everything below it. For a self-signed server certificate this is the server's certificate itself.
    /// </summary>
    public string? RootThumbprint { get; set; }

    public string? RootSubject { get; set; }

    /// <summary>
    /// A sentence naming what to do about it, shown alongside the certificate.
    /// </summary>
    public string? Remediation { get; set; }

    public bool IsExpired => ValidTo.HasValue && ValidTo.Value < DateTime.UtcNow;

    public bool IsNotYetValid => ValidFrom.HasValue && ValidFrom.Value > DateTime.UtcNow;
}
