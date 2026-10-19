// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Security;

/// <summary>
/// Whether JIM can sign people in through its identity provider, as far as the identity provider's TLS certificate
/// decides it.
/// </summary>
public enum IdentityProviderTrustState
{
    /// <summary>
    /// JIM has not checked yet (it checks shortly after starting).
    /// </summary>
    NotChecked = 0,

    /// <summary>
    /// The identity provider's certificate is trusted, by the operating system or by the JIM certificate store.
    /// </summary>
    Trusted = 1,

    /// <summary>
    /// Nothing JIM trusts vouches for the identity provider's certificate. Trusting its certificate authority for
    /// sign-in fixes it.
    /// </summary>
    Untrusted = 2,

    /// <summary>
    /// The certificate does not name the host JIM connects to. Trusting a certificate cannot fix this; the
    /// identity provider's certificate, or JIM_SSO_AUTHORITY, has to change.
    /// </summary>
    NameMismatch = 3,

    /// <summary>
    /// The identity provider could not be reached at all.
    /// </summary>
    Unreachable = 4,

    /// <summary>
    /// The authority is plain HTTP, so there is no certificate to trust. Only suitable for local development.
    /// </summary>
    NotHttps = 5
}

/// <summary>
/// What happened when an administrator asked JIM to trust a certificate its identity provider presented.
/// </summary>
public enum IdentityProviderCertificateTrustOutcome
{
    /// <summary>
    /// The certificate was added to the JIM certificate store, trusted for sign-in.
    /// </summary>
    Trusted = 0,

    /// <summary>
    /// The certificate was already in the store for Connectors and is now trusted for sign-in as well.
    /// </summary>
    TrustedForSignInAsWell = 1,

    /// <summary>
    /// The certificate was already trusted for sign-in, so nothing changed.
    /// </summary>
    AlreadyTrusted = 2,

    /// <summary>
    /// No thumbprint was given, or the identity provider is not presenting a certificate with that thumbprint now.
    /// Nothing was trusted.
    /// </summary>
    ThumbprintNotPresented = 3,

    /// <summary>
    /// The certificate does not name the identity provider's host, which trusting cannot fix. Nothing was trusted.
    /// </summary>
    NameMismatch = 4,

    /// <summary>
    /// The chain is invalid however it is anchored (an expired certificate, a broken signature), so trusting the
    /// certificate would change nothing. Nothing was trusted.
    /// </summary>
    InvalidChain = 5,

    /// <summary>
    /// The identity provider could not be reached, or uses plain HTTP, or no authority is configured. Nothing was trusted.
    /// </summary>
    IdentityProviderUnavailable = 6
}
