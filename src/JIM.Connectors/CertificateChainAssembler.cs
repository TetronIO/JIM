// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;
using System.Security.Cryptography.X509Certificates;

namespace JIM.Connectors;

/// <summary>
/// Assembles the certificate chain behind a server's certificate, and works out what it would take to trust it.
/// </summary>
/// <remarks>
/// <para>
/// The chain is built from everything JIM can lay hands on, so an administrator is shown as much of it as can be
/// found: what the server sent, what JIM already holds, and what can be downloaded from the address each certificate
/// names for its issuer (Authority Information Access). Revocation is not checked, matching the connections
/// themselves, because air-gapped deployments cannot reach a revocation list or responder.
/// </para>
/// <para>
/// Whether the chain is trusted is judged more narrowly, on what a connection would have: what the server sent and
/// what the JIM certificate store holds (<see cref="JimCertificateStoreTrust"/>), or else the operating system's own
/// trust. A certificate JIM had to download does not count until it is in the store.
/// </para>
/// </remarks>
internal static class CertificateChainAssembler
{
    /// <summary>
    /// Assembles the chain behind <paramref name="leaf"/>.
    /// </summary>
    /// <param name="leaf">The server's certificate.</param>
    /// <param name="sentByServer">The other certificates the server sent during the handshake.</param>
    /// <param name="trustedCertificates">The JIM certificate store.</param>
    /// <param name="downloadTimeout">How long to wait for each issuer certificate JIM downloads.</param>
    internal static AssembledCertificateChain Assemble(
        X509Certificate2 leaf,
        IReadOnlyCollection<X509Certificate2> sentByServer,
        IReadOnlyCollection<X509Certificate2> trustedCertificates,
        TimeSpan downloadTimeout)
    {
        ArgumentNullException.ThrowIfNull(leaf);

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.UrlRetrievalTimeout = downloadTimeout;
        chain.ChainPolicy.DisableCertificateDownloads = false;
        chain.ChainPolicy.ExtraStore.AddRange(sentByServer.Concat(trustedCertificates).ToArray());
        chain.Build(leaf);

        var elements = new List<AssembledCertificateChainElement>();
        X509Certificate2? below = null;
        foreach (var element in chain.ChainElements)
        {
            // Copied out: the chain owns its elements and disposes them with itself.
            var certificate = X509CertificateLoader.LoadCertificate(element.Certificate.RawData);
            var (source, downloadedFrom) = elements.Count == 0
                ? (ServerCertificateChainElementSource.SentByServer, (string?)null)
                : SourceOf(certificate, below!, sentByServer, trustedCertificates);

            elements.Add(new AssembledCertificateChainElement(certificate, source, downloadedFrom));
            below = certificate;
        }

        var top = elements[^1].Certificate;
        var isComplete = IsSelfSigned(top) && !chain.ChainStatus.Any(status => status.Status == X509ChainStatusFlags.PartialChain);
        var root = isComplete ? top : null;

        return new AssembledCertificateChain(
            elements,
            root,
            missingIssuer: isComplete ? null : top.Issuer,
            isTrusted: IsTrusted(leaf, sentByServer, trustedCertificates),
            problem: FindProblem(elements));
    }

    /// <summary>
    /// Where a certificate in the chain came from, in the order an administrator would want it described: the
    /// server's own word first, then JIM's own store, then the network.
    /// </summary>
    private static (ServerCertificateChainElementSource Source, string? DownloadedFrom) SourceOf(
        X509Certificate2 certificate,
        X509Certificate2 below,
        IReadOnlyCollection<X509Certificate2> sentByServer,
        IReadOnlyCollection<X509Certificate2> trustedCertificates)
    {
        if (Contains(sentByServer, certificate))
            return (ServerCertificateChainElementSource.SentByServer, null);

        if (Contains(trustedCertificates, certificate))
            return (ServerCertificateChainElementSource.JimCertificateStore, null);

        // Not handed to the chain builder by anyone, so it found it itself: either at the address the certificate
        // below names for its issuer, or in the operating system's own stores.
        var downloadAddress = IssuerDownloadAddresses(below).FirstOrDefault();
        return downloadAddress != null
            ? (ServerCertificateChainElementSource.Downloaded, downloadAddress)
            : (ServerCertificateChainElementSource.OperatingSystem, null);
    }

    /// <summary>
    /// The addresses a certificate names for downloading its issuer's certificate, from its Authority Information
    /// Access extension. Only HTTP ones: those are the only kind the platform downloads from.
    /// </summary>
    internal static IEnumerable<string> IssuerDownloadAddresses(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions.OfType<X509AuthorityInformationAccessExtension>().FirstOrDefault();
        if (extension == null)
            return [];

        return extension.EnumerateCAIssuersUris()
            .Where(uri => uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Whether a connection would accept the chain: the JIM certificate store vouches for it, or the operating system
    /// trusts it. Judged on what the server sent, never on what JIM downloaded.
    /// </summary>
    private static bool IsTrusted(X509Certificate2 leaf, IReadOnlyCollection<X509Certificate2> sentByServer, IReadOnlyCollection<X509Certificate2> trustedCertificates)
    {
        if (JimCertificateStoreTrust.VouchesFor(leaf, sentByServer, trustedCertificates))
            return true;

        using var system = new X509Chain();
        system.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        system.ChainPolicy.DisableCertificateDownloads = true;
        system.ChainPolicy.ExtraStore.AddRange(sentByServer.ToArray());
        return system.Build(leaf);
    }

    /// <summary>
    /// Why the chain could not be relied on whatever JIM trusted, or null when it can. Checks the certificate
    /// authorities only: the server certificate's own dates and name are judged, and reported, by the caller.
    /// </summary>
    private static string? FindProblem(List<AssembledCertificateChainElement> elements)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ExtraStore.AddRange(elements.Select(element => element.Certificate).ToArray());
        chain.Build(elements[0].Certificate);

        // Index 0 is the server's certificate, whose dates are the caller's to report; everything above it is a
        // certificate authority this chain depends on.
        for (var index = 1; index < chain.ChainElements.Count; index++)
        {
            var element = chain.ChainElements[index];
            var name = PresentedServerCertificate.CommonNameOf(element.Certificate.Subject);
            foreach (var status in element.ChainElementStatus.Select(status => status.Status))
            {
                if (status.HasFlag(X509ChainStatusFlags.NotTimeValid))
                    return element.Certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow
                        ? $"{name} expired on {element.Certificate.NotAfter.ToUniversalTime():d MMM yyyy}. A chain through an expired certificate authority is never valid; it needs renewing."
                        : $"{name} is not valid until {element.Certificate.NotBefore.ToUniversalTime():d MMM yyyy}.";

                if (status.HasFlag(X509ChainStatusFlags.InvalidBasicConstraints))
                    return $"{name} is not marked as a certificate authority, so it cannot vouch for the certificate below it.";

                if (status.HasFlag(X509ChainStatusFlags.NotSignatureValid))
                    return $"The signature {name} made on the certificate below it does not verify.";
            }
        }

        // The chain builder reports constraint failures against the certificate that violates them, which can be the
        // issued one rather than the issuer, so look at the authorities themselves as well.
        foreach (var authority in elements.Skip(1).Select(element => element.Certificate))
        {
            var constraints = authority.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
            if (constraints is { CertificateAuthority: false })
                return $"{PresentedServerCertificate.CommonNameOf(authority.Subject)} is not marked as a certificate authority, so it cannot vouch for the certificate below it.";
        }

        return null;
    }

    private static bool Contains(IReadOnlyCollection<X509Certificate2> certificates, X509Certificate2 certificate) =>
        certificates.Any(candidate => string.Equals(candidate.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase));

    internal static bool IsSelfSigned(X509Certificate2 certificate) =>
        string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal);
}

/// <summary>
/// One certificate in an assembled chain, with where JIM found it.
/// </summary>
internal sealed record AssembledCertificateChainElement(X509Certificate2 Certificate, ServerCertificateChainElementSource Source, string? DownloadedFrom);

/// <summary>
/// A server's certificate chain as JIM assembled it. Owns, and disposes, every certificate it holds.
/// </summary>
internal sealed class AssembledCertificateChain : IDisposable
{
    internal AssembledCertificateChain(
        IReadOnlyList<AssembledCertificateChainElement> elements,
        X509Certificate2? root,
        string? missingIssuer,
        bool isTrusted,
        string? problem)
    {
        Elements = elements;
        Root = root;
        MissingIssuer = missingIssuer;
        IsTrusted = isTrusted;
        Problem = problem;
    }

    /// <summary>
    /// The chain, the server's certificate first, ending at the root where JIM reached one.
    /// </summary>
    internal IReadOnlyList<AssembledCertificateChainElement> Elements { get; }

    /// <summary>
    /// The root at the top of the chain, or null when JIM could not reach one.
    /// </summary>
    internal X509Certificate2? Root { get; }

    internal bool IsComplete => Root != null;

    /// <summary>
    /// The subject of the first certificate JIM could not find, when the chain is incomplete.
    /// </summary>
    internal string? MissingIssuer { get; }

    /// <summary>
    /// Whether a connection would accept the chain: a certificate in it is in the JIM certificate store, or the
    /// operating system trusts it.
    /// </summary>
    internal bool IsTrusted { get; }

    /// <summary>
    /// Why the chain cannot be relied on whatever JIM trusted, or null when it can.
    /// </summary>
    internal string? Problem { get; }

    public void Dispose()
    {
        foreach (var element in Elements)
            element.Certificate.Dispose();
    }
}
