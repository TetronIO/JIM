// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Connectors;

/// <summary>
/// How an attempt to read the certificate a Connected System's server presents turned out.
/// </summary>
public enum ServerCertificateReadOutcome
{
    /// <summary>
    /// The server was asked and answered; the reading describes what it presented.
    /// </summary>
    Read,

    /// <summary>
    /// No Connected System with that identifier exists.
    /// </summary>
    ConnectedSystemNotFound,

    /// <summary>
    /// The system is not configured for an encrypted connection, so there is no certificate to look at. Covers a
    /// connector that never makes one, and a connector that can but has not been configured to.
    /// </summary>
    NotConfiguredForSecureConnection,

    /// <summary>
    /// The server could not be reached, which is a connectivity problem rather than a certificate one.
    /// </summary>
    ServerUnreachable,

    /// <summary>
    /// A certificate the administrator supplied could not be read.
    /// </summary>
    InvalidCertificate
}

/// <summary>
/// How an attempt to trust the certificate a Connected System's server presents turned out.
/// </summary>
public enum ServerCertificateTrustOutcome
{
    /// <summary>
    /// The certificate was added to the JIM certificate store and the addition audited.
    /// </summary>
    Trusted,

    /// <summary>
    /// The certificate is already in the store, so there was nothing to do.
    /// </summary>
    AlreadyTrusted,

    /// <summary>
    /// Neither the certificate the server is presenting now nor the authority that issued it matches the thumbprint
    /// the administrator confirmed, so nothing was trusted. Expected after a renewal; worth investigating otherwise.
    /// </summary>
    ThumbprintMismatch,

    /// <summary>
    /// No Connected System with that identifier exists.
    /// </summary>
    ConnectedSystemNotFound,

    /// <summary>
    /// The system is not configured for an encrypted connection, so there is no certificate to trust.
    /// </summary>
    NotConfiguredForSecureConnection,

    /// <summary>
    /// The server could not be reached to read its certificate again, so nothing was trusted.
    /// </summary>
    ServerUnreachable,

    /// <summary>
    /// The thumbprint names a certificate in the chain that is not its root: the server's own certificate, or a
    /// certificate authority between it and the root. Trusting either would never make the connection work, so
    /// nothing was trusted; the result names the root to trust instead, where JIM has it.
    /// </summary>
    NotTheRoot,

    /// <summary>
    /// JIM could not complete the chain up to a root, even with any certificates supplied, so there is nothing that
    /// could be trusted to make the connection work. The result names the certificate that is missing.
    /// </summary>
    ChainIncomplete,

    /// <summary>
    /// A supplied certificate is not part of this server's chain. Nothing was trusted, so a mistaken file is never
    /// stored.
    /// </summary>
    CertificateNotInChain,

    /// <summary>
    /// A certificate in the chain cannot be relied on: a supplied file could not be read, a certificate authority in
    /// the chain is expired or not yet valid, is not marked as a certificate authority, or a signature does not
    /// verify. Nothing was trusted.
    /// </summary>
    InvalidCertificate
}
