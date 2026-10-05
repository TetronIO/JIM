// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;

namespace JIM.Web.Models;

/// <summary>
/// One certificate in a server's chain that an administrator can choose to trust.
/// </summary>
/// <remarks>
/// JIM accepts a server when any certificate in its chain is in the JIM certificate store, so every certificate in
/// the chain is a working choice (#1914). What differs is how long the decision lasts: the higher the certificate,
/// the more renewals it survives, which is why the options are ordered from the top.
/// </remarks>
public class ServerCertificateTrustOption
{
    public ServerCertificateChainElement Element { get; init; } = null!;

    public ServerCertificateChainRole Role { get; init; }

    /// <summary>
    /// The most durable choice, where there is more than one.
    /// </summary>
    public bool IsRecommended { get; init; }

    /// <summary>
    /// For a downloaded certificate, the common name of the certificate whose Authority Information Access extension
    /// named the address, so the administrator can judge where it came from.
    /// </summary>
    public string? AddressGivenBy { get; init; }

    /// <summary>
    /// Certificate authorities between the server's certificate and this one that JIM downloaded rather than the
    /// server sending, and which JIM therefore stores alongside it so connections can link the two.
    /// </summary>
    public List<ServerCertificateChainElement> AlsoStored { get; init; } = [];

    public string Thumbprint => Element.Thumbprint;

    public string CommonName => PresentedServerCertificate.CommonNameOf(Element.Subject);

    /// <summary>
    /// The certificates in the chain an administrator could trust, the most durable first.
    /// </summary>
    public static List<ServerCertificateTrustOption> For(ServerCertificateDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var chain = diagnostic.Chain.Count > 0 ? diagnostic.Chain : ChainRecordedBeforeJimDescribedChains(diagnostic);
        var options = new List<ServerCertificateTrustOption>();

        for (var index = chain.Count - 1; index >= 0; index--)
        {
            var element = chain[index];
            options.Add(new ServerCertificateTrustOption
            {
                Element = element,
                Role = element.IsSelfSigned ? ServerCertificateChainRole.Root
                    : index == 0 ? ServerCertificateChainRole.Server
                    : ServerCertificateChainRole.Intermediate,
                IsRecommended = chain.Count > 1 && index == chain.Count - 1,
                AddressGivenBy = element.Source == ServerCertificateChainElementSource.Downloaded && index > 0
                    ? PresentedServerCertificate.CommonNameOf(chain[index - 1].Subject)
                    : null,
                AlsoStored = chain.Take(index).Skip(1).Where(below => below.Source == ServerCertificateChainElementSource.Downloaded).ToList()
            });
        }

        return options;
    }

    /// <summary>
    /// Diagnostics recorded on Activities before JIM described chains carry only the server's certificate and, where
    /// the server sent it, the thumbprint of its issuer. The trust action reads the server again in any case.
    /// </summary>
    private static List<ServerCertificateChainElement> ChainRecordedBeforeJimDescribedChains(ServerCertificateDiagnostic diagnostic)
    {
        var chain = new List<ServerCertificateChainElement>
        {
            new()
            {
                Subject = diagnostic.Subject ?? string.Empty,
                Issuer = diagnostic.Issuer ?? string.Empty,
                Thumbprint = diagnostic.Thumbprint ?? string.Empty,
                ValidFrom = diagnostic.ValidFrom ?? default,
                ValidTo = diagnostic.ValidTo ?? default,
                IsSelfSigned = diagnostic.IsSelfSigned
            }
        };

        if (!string.IsNullOrEmpty(diagnostic.IssuerThumbprint) && !diagnostic.IsSelfSigned)
        {
            chain.Add(new ServerCertificateChainElement
            {
                Subject = diagnostic.Issuer ?? string.Empty,
                Thumbprint = diagnostic.IssuerThumbprint,
                IsCertificateAuthority = true
            });
        }

        return chain;
    }
}
