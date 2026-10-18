// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Checks whether JIM can trust its identity provider's TLS certificate, and trusts a certificate the identity
/// provider presents when an administrator asks (#2363).
/// </summary>
/// <remarks>
/// <para>
/// Trusting is pinned to a thumbprint the identity provider is presenting at the moment of the decision: the chain is
/// read again for every request, so neither an arbitrary certificate nor one the identity provider stopped presenting
/// can be trusted this way. A certificate that would not make the identity provider trusted (a name mismatch, an
/// expired certificate in the chain) is refused rather than stored, because storing it would change nothing.
/// </para>
/// <para>
/// Who may do this is the caller's business: every surface that reaches it requires the Administrator role.
/// </para>
/// </remarks>
public class IdentityProviderTrustServer
{
    private JimApplication Application { get; }

    /// <summary>
    /// Connects to the identity provider. Settable so checking and trusting can be tested without a TLS server.
    /// </summary>
    internal IIdentityProviderProbe Probe { get; set; } = new IdentityProviderProbe();

    internal IdentityProviderTrustServer(JimApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// Where an authority publishes its OpenID Connect configuration.
    /// </summary>
    public static string MetadataAddressFor(string authority) => $"{authority.TrimEnd('/')}/.well-known/openid-configuration";

    /// <summary>
    /// Connects to the identity provider and reports whether its certificate is trusted, by what, and the chain it
    /// presented.
    /// </summary>
    public async Task<IdentityProviderTrustStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var (status, probe) = await CheckCoreAsync(cancellationToken);
        DisposeCertificates(probe);
        return status;
    }

    /// <summary>
    /// Adds a certificate the identity provider presents to the JIM certificate store, trusted for sign-in.
    /// </summary>
    /// <param name="thumbprint">The thumbprint the administrator confirmed, in any common format.</param>
    /// <param name="initiatedBy">The administrator, recorded on the Activity.</param>
    /// <param name="name">What to call it in the store. Its common name when not given.</param>
    /// <param name="changeReason">Why, recorded on the Activity. A sentence naming the identity provider when not given.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public Task<IdentityProviderCertificateTrustResult> TrustPresentedCertificateAsync(string thumbprint, MetaverseObject? initiatedBy,
        string? name = null, string? changeReason = null, CancellationToken cancellationToken = default)
    {
        return TrustPresentedCertificateCoreAsync(thumbprint, name, changeReason, cancellationToken,
            (certificateName, data, notes, reason) => Application.Certificates.AddFromDataAsync(certificateName, data, initiatedBy, notes, reason, CertificateTrustPurpose.SignIn),
            (id, trustedFor, reason) => Application.Certificates.UpdateAsync(id, initiatedBy, changeReason: reason, trustedFor: trustedFor));
    }

    /// <summary>
    /// Adds a certificate the identity provider presents to the JIM certificate store, trusted for sign-in. API-key
    /// initiator overload: the infrastructure API key is how the first certificate is trusted, before anyone can sign in.
    /// </summary>
    /// <inheritdoc cref="TrustPresentedCertificateAsync(string, MetaverseObject?, string?, string?, CancellationToken)" path="/param"/>
    public Task<IdentityProviderCertificateTrustResult> TrustPresentedCertificateAsync(string thumbprint, ApiKey initiatedByApiKey,
        string? name = null, string? changeReason = null, CancellationToken cancellationToken = default)
    {
        return TrustPresentedCertificateCoreAsync(thumbprint, name, changeReason, cancellationToken,
            (certificateName, data, notes, reason) => Application.Certificates.AddFromDataAsync(certificateName, data, initiatedByApiKey, notes, reason, CertificateTrustPurpose.SignIn),
            (id, trustedFor, reason) => Application.Certificates.UpdateAsync(id, initiatedByApiKey, changeReason: reason, trustedFor: trustedFor));
    }

    private async Task<IdentityProviderCertificateTrustResult> TrustPresentedCertificateCoreAsync(
        string thumbprint,
        string? name,
        string? changeReason,
        CancellationToken cancellationToken,
        Func<string, byte[], string?, string?, Task<TrustedCertificate>> addAsync,
        Func<Guid, CertificateTrustPurpose, string, Task> updateTrustedForAsync)
    {
        var expected = NormaliseThumbprint(thumbprint);
        if (expected.Length == 0)
        {
            return new IdentityProviderCertificateTrustResult
            {
                Outcome = IdentityProviderCertificateTrustOutcome.ThumbprintNotPresented,
                Message = "A thumbprint is required. JIM will not trust whatever the identity provider happens to be presenting."
            };
        }

        var (status, probe) = await CheckCoreAsync(cancellationToken);
        try
        {
            switch (status.State)
            {
                case IdentityProviderTrustState.NotChecked or IdentityProviderTrustState.Unreachable or IdentityProviderTrustState.NotHttps:
                    return Result(IdentityProviderCertificateTrustOutcome.IdentityProviderUnavailable, status, $"Nothing was trusted. {status.Message}");
                case IdentityProviderTrustState.NameMismatch:
                    return Result(IdentityProviderCertificateTrustOutcome.NameMismatch, status, $"Nothing was trusted. {status.Message}");
            }

            var chosen = status.PresentedChain.FirstOrDefault(c => NormaliseThumbprint(c.Thumbprint) == expected);
            if (chosen == null)
            {
                Log.Warning("Refused to trust a certificate for sign-in: {Host} is not presenting {Expected}", LogSanitiser.Sanitise(status.Host), LogSanitiser.Sanitise(expected));
                return Result(IdentityProviderCertificateTrustOutcome.ThumbprintNotPresented, status,
                    $"{status.Host} is not presenting a certificate with that thumbprint now, so nothing was trusted. This is expected if its certificate was renewed since you looked; check its status again.");
            }

            var existing = await Application.Repository.TrustedCertificates.GetByThumbprintAsync(chosen.Thumbprint);
            if (existing != null && existing.TrustedFor.HasFlag(CertificateTrustPurpose.SignIn))
                return Result(IdentityProviderCertificateTrustOutcome.AlreadyTrusted, status, $"'{existing.Name}' is already trusted for sign-in.", existing);

            if (!WouldVouchForTheIdentityProvider(chosen, probe))
            {
                return Result(IdentityProviderCertificateTrustOutcome.InvalidChain, status,
                    $"Trusting '{chosen.CommonName}' would not make {status.Host} trusted: a certificate in its chain has expired or is not valid. Nothing was trusted; the identity provider's certificate needs renewing.");
            }

            var reason = string.IsNullOrWhiteSpace(changeReason)
                ? $"Trusted the certificate the identity provider {status.Host} presents, so JIM can sign people in through it."
                : changeReason;

            if (existing != null)
            {
                await updateTrustedForAsync(existing.Id, existing.TrustedFor | CertificateTrustPurpose.SignIn, reason);
                var widened = await Application.Repository.TrustedCertificates.GetByIdAsync(existing.Id) ?? existing;
                return Result(IdentityProviderCertificateTrustOutcome.TrustedForSignInAsWell, status,
                    $"'{widened.Name}' was already trusted for Connectors and is now trusted for sign-in as well.", widened);
            }

            var notes = $"Trusted for sign-in from the identity provider {status.Host}, which presented it.";
            var added = await addAsync(string.IsNullOrWhiteSpace(name) ? chosen.CommonName : name.Trim(), chosen.Data, notes, reason);
            return Result(IdentityProviderCertificateTrustOutcome.Trusted, status, $"'{added.Name}' is now trusted for sign-in.", added);
        }
        finally
        {
            DisposeCertificates(probe);
        }
    }

    private async Task<(IdentityProviderTrustStatus Status, IdentityProviderProbeResult? Probe)> CheckCoreAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var authority = (await Application.ServiceSettings.GetServiceSettingsAsync())?.SSOAuthority;
        if (string.IsNullOrWhiteSpace(authority) || !Uri.TryCreate(MetadataAddressFor(authority), UriKind.Absolute, out var metadataAddress))
        {
            return (new IdentityProviderTrustStatus
            {
                State = IdentityProviderTrustState.NotChecked,
                Authority = authority,
                CheckedUtc = now,
                Message = "No identity provider is configured (JIM_SSO_AUTHORITY)."
            }, null);
        }

        var signInCertificates = await Application.Certificates.GetSignInTrustedCertificatesAsync();
        IdentityProviderProbeResult probe;
        try
        {
            probe = await Probe.ProbeAsync(metadataAddress, signInCertificates, cancellationToken);
        }
        finally
        {
            foreach (var certificate in signInCertificates)
                certificate.Dispose();
        }

        var store = await Application.Repository.TrustedCertificates.GetAllAsync();
        return (BuildStatus(authority, metadataAddress, probe, store, now), probe);
    }

    private static IdentityProviderTrustStatus BuildStatus(string authority, Uri metadataAddress, IdentityProviderProbeResult probe,
        List<TrustedCertificate> store, DateTime now)
    {
        var host = metadataAddress.Host;
        var byThumbprint = store
            .GroupBy(c => c.Thumbprint, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var chain = OrderChain(probe.ServerCertificate, probe.SentByServer);
        var presented = chain.Select((certificate, index) => Describe(certificate, index == 0, byThumbprint)).ToList();
        var top = chain.LastOrDefault();
        var unsentIssuer = top != null && top.SubjectName.RawData.AsSpan().SequenceEqual(top.IssuerName.RawData) ? null : top?.Issuer;

        var decision = probe.Decision;
        var vouching = (decision?.VouchingThumbprints ?? [])
            .Select(t => presented.FirstOrDefault(p => string.Equals(p.Thumbprint, t, StringComparison.OrdinalIgnoreCase))
                ?? (byThumbprint.TryGetValue(t, out var stored) ? Describe(stored) : null))
            .Where(c => c != null)
            .Select(c => c!)
            .ToList();

        var (state, message) = Judge(metadataAddress, probe, presented, unsentIssuer, vouching);

        return new IdentityProviderTrustStatus
        {
            State = state,
            Authority = authority,
            Host = host,
            TrustedBy = state == IdentityProviderTrustState.Trusted ? decision?.Outcome : null,
            PresentedChain = presented,
            UnsentIssuer = unsentIssuer,
            UnsentIssuerCommonName = unsentIssuer == null ? null : CommonNameOf(unsentIssuer),
            VouchingCertificates = state == IdentityProviderTrustState.Trusted ? vouching : [],
            Message = message,
            CheckedUtc = now
        };
    }

    private static (IdentityProviderTrustState State, string Message) Judge(Uri metadataAddress, IdentityProviderProbeResult probe,
        List<IdentityProviderCertificate> presented, string? unsentIssuer, List<IdentityProviderCertificate> vouching)
    {
        var host = metadataAddress.Host;

        if (!probe.Reached)
            return (IdentityProviderTrustState.Unreachable, $"JIM could not reach the identity provider at {host}: {probe.Error ?? "no answer"}.");

        if (metadataAddress.Scheme == Uri.UriSchemeHttp)
        {
            return probe.DocumentRead
                ? (IdentityProviderTrustState.NotHttps, $"The identity provider at {host} is reached over plain HTTP, so there is no certificate to check. Use HTTPS outside local development.")
                : (IdentityProviderTrustState.Unreachable, $"JIM reached {host} but could not read its OpenID Connect configuration: {probe.Error ?? "no answer"}.");
        }

        switch (probe.Decision?.Outcome)
        {
            case CertificateStoreTrustOutcome.NameMismatch:
                return (IdentityProviderTrustState.NameMismatch,
                    $"{host}'s certificate was not issued for {host}. Trusting a certificate cannot fix this: the identity provider needs a certificate that names {host}, or JIM_SSO_AUTHORITY must use a name its certificate carries.");
            case CertificateStoreTrustOutcome.Untrusted:
                var top = presented.LastOrDefault();
                var issuedBy = unsentIssuer == null ? string.Empty : $" (issued by \"{CommonNameOf(unsentIssuer)}\")";
                return (IdentityProviderTrustState.Untrusted,
                    $"JIM does not trust {host}'s certificate: its chain ends at \"{top?.CommonName}\"{issuedBy}, and nothing trusted for sign-in vouches for it. Trust that certificate authority for sign-in to enable sign-in.");
            case CertificateStoreTrustOutcome.NoCertificate or null:
                return (IdentityProviderTrustState.Unreachable, $"JIM reached {host} but it presented no TLS certificate: {probe.Error ?? "no answer"}.");
        }

        if (!probe.DocumentRead)
            return (IdentityProviderTrustState.Unreachable, $"JIM trusts {host}'s certificate but could not read its OpenID Connect configuration: {probe.Error ?? "no answer"}. Check JIM_SSO_AUTHORITY.");

        return probe.Decision.Outcome == CertificateStoreTrustOutcome.TrustedByOperatingSystem
            ? (IdentityProviderTrustState.Trusted, $"{host}'s certificate is trusted by the operating system.")
            : (IdentityProviderTrustState.Trusted, $"{host}'s certificate is trusted through the JIM certificate store ({string.Join(", ", vouching.Select(v => $"'{v.CommonName}'"))}).");
    }

    /// <summary>
    /// Whether trusting this certificate alone would make the identity provider's chain trusted: false for a chain
    /// that is invalid however it is anchored.
    /// </summary>
    private static bool WouldVouchForTheIdentityProvider(IdentityProviderCertificate chosen, IdentityProviderProbeResult? probe)
    {
        if (probe?.ServerCertificate == null)
            return false;

        using var anchor = X509CertificateLoader.LoadCertificate(chosen.Data);
        return CertificateStoreTrust.VouchesFor(probe.ServerCertificate, probe.SentByServer, [anchor]);
    }

    /// <summary>
    /// The presented certificates from the server's own upwards, following each certificate's issuer among what the
    /// server sent.
    /// </summary>
    private static List<X509Certificate2> OrderChain(X509Certificate2? serverCertificate, IReadOnlyList<X509Certificate2> sentByServer)
    {
        if (serverCertificate == null)
            return [];

        var chain = new List<X509Certificate2> { serverCertificate };
        var current = serverCertificate;
        while (!current.SubjectName.RawData.AsSpan().SequenceEqual(current.IssuerName.RawData))
        {
            var issuedBy = current;
            var issuer = sentByServer.FirstOrDefault(c =>
                c.SubjectName.RawData.AsSpan().SequenceEqual(issuedBy.IssuerName.RawData) &&
                chain.TrueForAll(already => !string.Equals(already.Thumbprint, c.Thumbprint, StringComparison.OrdinalIgnoreCase)));
            if (issuer == null)
                break;

            chain.Add(issuer);
            current = issuer;
        }

        return chain;
    }

    private static IdentityProviderCertificate Describe(X509Certificate2 certificate, bool isServerCertificate, Dictionary<string, TrustedCertificate> store)
    {
        store.TryGetValue(certificate.Thumbprint, out var stored);
        return new IdentityProviderCertificate
        {
            Subject = certificate.Subject,
            CommonName = CommonNameOf(certificate.Subject),
            Issuer = certificate.Issuer,
            Thumbprint = certificate.Thumbprint,
            ValidFrom = certificate.NotBefore.ToUniversalTime(),
            ValidTo = certificate.NotAfter.ToUniversalTime(),
            IsServerCertificate = isServerCertificate,
            TrustedCertificateId = stored?.Id,
            TrustedFor = stored?.TrustedFor,
            Data = certificate.RawData
        };
    }

    /// <summary>
    /// A store certificate that vouches for the identity provider without the identity provider having sent it (a root).
    /// </summary>
    private static IdentityProviderCertificate Describe(TrustedCertificate stored) => new()
    {
        Subject = stored.Subject,
        CommonName = stored.Name,
        Issuer = stored.Issuer,
        Thumbprint = stored.Thumbprint,
        ValidFrom = stored.ValidFrom,
        ValidTo = stored.ValidTo,
        TrustedCertificateId = stored.Id,
        TrustedFor = stored.TrustedFor
    };

    private static string CommonNameOf(string distinguishedName)
    {
        try
        {
            var commonName = new X500DistinguishedName(distinguishedName).EnumerateRelativeDistinguishedNames()
                .FirstOrDefault(rdn => rdn.GetSingleElementType().Value == "2.5.4.3")?.GetSingleElementValue();
            return string.IsNullOrWhiteSpace(commonName) ? distinguishedName : commonName;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return distinguishedName;
        }
    }

    /// <summary>
    /// Thumbprints are quoted with spaces or colons between the pairs depending on where they were copied from, so
    /// compare them without.
    /// </summary>
    private static string NormaliseThumbprint(string? thumbprint) =>
        string.IsNullOrWhiteSpace(thumbprint) ? string.Empty : new string(thumbprint.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();

    private static IdentityProviderCertificateTrustResult Result(IdentityProviderCertificateTrustOutcome outcome, IdentityProviderTrustStatus status,
        string message, TrustedCertificate? certificate = null) => new()
    {
        Outcome = outcome,
        Certificate = certificate,
        Message = message,
        Status = status
    };

    private static void DisposeCertificates(IdentityProviderProbeResult? probe)
    {
        if (probe == null)
            return;

        probe.ServerCertificate?.Dispose();
        foreach (var certificate in probe.SentByServer)
            certificate.Dispose();
    }
}
