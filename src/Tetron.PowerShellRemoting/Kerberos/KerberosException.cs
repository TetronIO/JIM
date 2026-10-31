// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Tetron.PowerShellRemoting.Kerberos.Native;

namespace Tetron.PowerShellRemoting.Kerberos;

/// <summary>
/// A Kerberos (GSSAPI) call failed. The message carries GSSAPI's own description of both status codes, so an
/// administrator can tell a wrong password from clock skew or an unknown service principal. It never carries a secret.
/// </summary>
public sealed class KerberosException : Exception
{
    public KerberosException(string operation, uint majorStatus, uint minorStatus, string description)
        : base($"Kerberos {operation} failed: {description} (major 0x{majorStatus:X8}, minor 0x{minorStatus:X8}).")
    {
        Operation = operation;
        MajorStatus = majorStatus;
        MinorStatus = minorStatus;
    }

    /// <summary>The GSSAPI routine that failed, for example <c>gss_init_sec_context</c>.</summary>
    public string Operation { get; }

    /// <summary>The GSSAPI major status code.</summary>
    public uint MajorStatus { get; }

    /// <summary>The mechanism (Kerberos) minor status code.</summary>
    public uint MinorStatus { get; }

    /// <summary>
    /// True when the ticket-granting ticket has expired, so a new login would succeed: GSSAPI's own
    /// <c>GSS_S_CREDENTIALS_EXPIRED</c>, or the KDC refusing a service ticket with <c>KRB5KRB_AP_ERR_TKT_EXPIRED</c>.
    /// </summary>
    public bool IsCredentialExpired =>
        (MajorStatus & GssapiNative.GssRoutineErrorMask) == GssapiNative.GSS_S_CREDENTIALS_EXPIRED
        || MinorStatus == GssapiNative.KRB5KRB_AP_ERR_TKT_EXPIRED;
}
