// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Connectors;

/// <summary>
/// Why a server's certificate was refused. Each value maps to a different thing an administrator has to do, which is
/// the point of distinguishing them.
/// </summary>
public enum ServerCertificateFailureReason
{
    /// <summary>
    /// Nothing is wrong with the certificate; the connection failed for another reason.
    /// </summary>
    None = 0,

    /// <summary>
    /// The issuer is trusted by neither the operating system nor the JIM certificate store. Resolved by adding the
    /// issuing certificate authority, or the certificate itself when self-signed, to the JIM certificate store.
    /// </summary>
    UntrustedIssuer = 1,

    /// <summary>
    /// The certificate was issued for a different name than the one being connected to. Adding it to the JIM
    /// certificate store does not help; the host has to be reached by a name the certificate carries.
    /// </summary>
    NameMismatch = 2,

    /// <summary>
    /// The certificate's validity period has passed. Trusting its issuer does not waive this.
    /// </summary>
    Expired = 3,

    /// <summary>
    /// The certificate's validity period has not started yet, which usually means clock skew between JIM and the
    /// directory server.
    /// </summary>
    NotYetValid = 4,

    /// <summary>
    /// The server offered no certificate at all, so the connection could not be encrypted.
    /// </summary>
    NoCertificatePresented = 5,

    /// <summary>
    /// A certificate was presented and refused, but for a reason JIM could not narrow down.
    /// </summary>
    Unknown = 6
}
