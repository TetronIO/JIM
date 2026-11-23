// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Connectors.Resources;
using JIM.Models.Localisation;
using Tetron.PowerShellRemoting;
using Tetron.PowerShellRemoting.Kerberos;
using Tetron.PowerShellRemoting.WSMan;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Words for why signing in to Exchange failed when the LDAP Connector's settings were checked (#2021, PRD requirement
/// 2): each reason as an administrator can act on it, naming the account, the server or the domain controller.
/// </summary>
/// <remarks>
/// Kerberos reasons are read from the Kerberos error code GSSAPI reports as its minor status, never from the text, which
/// differs between Kerberos libraries and releases.
/// </remarks>
internal static class LdapExchangeCheckFailures
{
    // MIT Kerberos error table: KRB5KDC_ERR_NONE is 0x96C73A00, and the protocol's error numbers follow it.
    private const uint KerberosErrorBase = 0x96C73A00;
    private const uint ClientUnknown = KerberosErrorBase + 6;
    private const uint ServiceUnknown = KerberosErrorBase + 7;
    private const uint ClientRevoked = KerberosErrorBase + 18;
    private const uint PasswordExpired = KerberosErrorBase + 23;
    private const uint PreauthenticationFailed = KerberosErrorBase + 24;
    private const uint IntegrityCheckFailed = KerberosErrorBase + 31;
    private const uint ClockSkew = KerberosErrorBase + 37;
    private const uint DomainControllerUnreachable = KerberosErrorBase + 0x9C;

    /// <summary>
    /// What to tell the administrator, given what opening the Exchange session threw.
    /// </summary>
    internal static string Describe(Exception error, LdapExchangeKerberosSignIn signIn, Uri endpoint) =>
        DescribeMessage(error, signIn, endpoint).ToEnglish();

    /// <summary>
    /// The same words as <see cref="Describe"/>, held as a message so the portal can show them in the viewer's language
    /// (#3030). What another system said (Exchange's own words, a Kerberos library's) stays in its own language.
    /// </summary>
    internal static JimMessage DescribeMessage(Exception error, LdapExchangeKerberosSignIn signIn, Uri endpoint)
    {
        if (Find<KerberosException>(error) is { } kerberos)
            return DescribeKerberos(kerberos, signIn, endpoint);

        var failure = RemoteFailure.FromException(error);
        var address = endpoint.ToString();
        return failure.Kind switch
        {
            RemoteFailureKind.Authentication when Find<WSManFaultException>(error) is { } refusal =>
                Message(nameof(ConnectorMessages.LdapExchangeRefusedRemotePowerShell), signIn.Principal, Sentence(refusal.Message)),
            RemoteFailureKind.Connection =>
                Message(nameof(ConnectorMessages.LdapExchangeUnreachable), address, Sentence(failure.Message), endpoint.Port.ToString(CultureInfo.InvariantCulture)),
            RemoteFailureKind.Timeout =>
                Message(nameof(ConnectorMessages.LdapExchangeTimedOut), address, Sentence(failure.Message)),
            RemoteFailureKind.Certificate =>
                Message(nameof(ConnectorMessages.LdapExchangeCertificateUntrusted), address, Sentence(failure.Message)),
            _ => Message(nameof(ConnectorMessages.LdapExchangeSessionRefused), address, failure.Message)
        };
    }

    private static JimMessage DescribeKerberos(KerberosException kerberos, LdapExchangeKerberosSignIn signIn, Uri endpoint) => kerberos.MinorStatus switch
    {
        ClockSkew => Message(nameof(ConnectorMessages.LdapKerberosClockSkew), signIn.Kdc),
        PreauthenticationFailed or IntegrityCheckFailed => Message(nameof(ConnectorMessages.LdapKerberosPasswordRefused), signIn.Principal),
        ClientUnknown => Message(nameof(ConnectorMessages.LdapKerberosAccountUnknown), signIn.Kdc, signIn.Principal),
        ServiceUnknown => Message(nameof(ConnectorMessages.LdapKerberosServiceUnknown), endpoint.Host),
        ClientRevoked => Message(nameof(ConnectorMessages.LdapKerberosAccountDisabled), signIn.Principal),
        PasswordExpired => Message(nameof(ConnectorMessages.LdapKerberosPasswordExpired), signIn.Principal),
        DomainControllerUnreachable => Message(nameof(ConnectorMessages.LdapKerberosDomainControllerUnreachable), signIn.Kdc),
        _ => Message(nameof(ConnectorMessages.LdapKerberosRefused), signIn.Principal, signIn.Kdc, kerberos.Message)
    };

    private static JimMessage Message(string key, params MessageArgument[] arguments) =>
        JimMessage.Create(ConnectorMessageCatalogues.Connectors, key, arguments);

    /// <summary>
    /// Another system's words, ended as a sentence so the advice that follows them reads as its own.
    /// </summary>
    private static string Sentence(string words)
    {
        var trimmed = words.Trim();
        return trimmed.Length == 0 || trimmed[^1] is '.' or '!' or '?' ? trimmed : $"{trimmed}.";
    }

    private static T? Find<T>(Exception error) where T : Exception
    {
        for (var cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is T found)
                return found;
        }

        return null;
    }
}
