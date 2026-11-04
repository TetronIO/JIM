// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
    internal static string Describe(Exception error, LdapExchangeKerberosSignIn signIn, Uri endpoint)
    {
        if (Find<KerberosException>(error) is { } kerberos)
            return DescribeKerberos(kerberos, signIn, endpoint);

        var failure = RemoteFailure.FromException(error);
        return failure.Kind switch
        {
            RemoteFailureKind.Authentication when Find<WSManFaultException>(error) is { } refusal =>
                $"Exchange refused {signIn.Principal}: {refusal.Message} The account signed in to the domain, but Exchange does not let it use remote PowerShell. " +
                "Add it to Exchange's Recipient Management role group, and check its remote PowerShell access is not turned off.",
            RemoteFailureKind.Connection =>
                $"JIM could not reach Exchange at {endpoint}: {failure.Message} Check the Exchange server's name, and that this server can reach it on port {endpoint.Port}.",
            RemoteFailureKind.Timeout =>
                $"Exchange at {endpoint} did not answer: {failure.Message} Check the server is running, and that nothing between this server and it is holding the connection.",
            RemoteFailureKind.Certificate =>
                $"JIM does not trust Exchange's certificate at {endpoint}: {failure.Message} Add the certificate authority that issued it in Certificates, " +
                "and use the server's name as its certificate gives it.",
            _ => $"Exchange at {endpoint} refused the session: {failure.Message}"
        };
    }

    private static string DescribeKerberos(KerberosException kerberos, LdapExchangeKerberosSignIn signIn, Uri endpoint) => kerberos.MinorStatus switch
    {
        ClockSkew =>
            $"This server's clock differs from the domain's by more than five minutes, so Kerberos refused the sign-in. " +
            $"Correct the time on this server, or on the domain controller {signIn.Kdc}, then save again.",
        PreauthenticationFailed or IntegrityCheckFailed =>
            $"The domain refused the password for {signIn.Principal}. Check the Password above: JIM signs in to Exchange with the directory account.",
        ClientUnknown =>
            $"The domain controller {signIn.Kdc} does not know the account {signIn.Principal}, so it cannot sign in to Exchange.",
        ServiceUnknown =>
            $"The domain has no service principal HTTP/{endpoint.Host}, so Kerberos cannot sign in to it. " +
            "Use the Exchange server's own DNS name, not an alias or an IP address, as the Exchange server.",
        ClientRevoked =>
            $"The account {signIn.Principal} is disabled or locked out, so the domain refused the sign-in.",
        PasswordExpired =>
            $"The account {signIn.Principal} cannot sign in because its password has expired. Set a new one, and enter it as the Password above.",
        DomainControllerUnreachable =>
            $"JIM could not reach the domain controller {signIn.Kdc} on port 88 to sign in to Exchange with Kerberos. Check this server can reach it.",
        _ => $"Kerberos refused the sign-in for {signIn.Principal} through {signIn.Kdc}: {kerberos.Message}"
    };

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
