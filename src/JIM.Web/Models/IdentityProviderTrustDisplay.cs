// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Security;
using MudBlazor;

namespace JIM.Web.Models;

/// <summary>
/// What the administrators' banner says about the identity provider (#2363). It speaks only when new sign-ins will
/// fail, or will soon: people already signed in keep working until their session ends, so an administrator who is
/// signed in is the one person who can still put it right from the portal.
/// </summary>
public static class IdentityProviderTrustDisplay
{
    /// <summary>
    /// What the banner says, or null when it should not be shown.
    /// </summary>
    public static IdentityProviderTrustBannerContent? Banner(IdentityProviderTrustStatus status, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(status);

        switch (status.State)
        {
            case IdentityProviderTrustState.Untrusted:
                var top = status.PresentedChain.LastOrDefault()?.CommonName;
                var from = top == null ? string.Empty : $" from \"{top}\"";
                return new IdentityProviderTrustBannerContent(Severity.Warning,
                    $"New sign-ins will fail. Your identity provider, {status.Host}, presents a certificate{from}, which JIM does not trust. Anyone already signed in can carry on until their session ends.",
                    OfferToTrust: status.SuggestedCertificate != null, LinkToCertificates: false);

            case IdentityProviderTrustState.NameMismatch:
                return new IdentityProviderTrustBannerContent(Severity.Warning,
                    $"New sign-ins will fail. Your identity provider's certificate does not name {status.Host}, which trusting a certificate cannot fix.",
                    OfferToTrust: false, LinkToCertificates: false);

            case IdentityProviderTrustState.Unreachable:
                return new IdentityProviderTrustBannerContent(Severity.Warning,
                    $"New sign-ins will fail while JIM cannot reach your identity provider at {status.Host}. Anyone already signed in can carry on until their session ends.",
                    OfferToTrust: false, LinkToCertificates: false);

            case IdentityProviderTrustState.Trusted when status.TrustExpiresSoon(utcNow):
                var lastToExpire = status.VouchingCertificates.MaxBy(c => c.ValidTo)!;
                return new IdentityProviderTrustBannerContent(Severity.Info,
                    $"Sign-in depends on \"{lastToExpire.CommonName}\", which expires on {lastToExpire.ValidTo.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}. Trust its replacement for sign-in before then.",
                    OfferToTrust: false, LinkToCertificates: true);

            default:
                return null;
        }
    }
}

/// <summary>
/// What the identity provider banner shows.
/// </summary>
/// <param name="Severity">Warning when new sign-ins fail, Info when they soon will.</param>
/// <param name="Sentence">The text, already complete.</param>
/// <param name="OfferToTrust">Whether trusting a certificate the identity provider presents would fix it, so the banner offers to.</param>
/// <param name="LinkToCertificates">Whether to link the Certificates page.</param>
public sealed record IdentityProviderTrustBannerContent(Severity Severity, string Sentence, bool OfferToTrust, bool LinkToCertificates);
