// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using JIM.Connectors.LDAP;
using NUnit.Framework;
using Tetron.PowerShellRemoting.Kerberos;
using Tetron.PowerShellRemoting.WSMan;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// What the Exchange settings check tells an administrator when signing in to Exchange fails (#2021, PRD requirement
/// 2): each reason in words they can act on, naming the account, the server or the domain controller concerned.
/// Kerberos reasons are read from the status code, never from text, so a different Kerberos library's wording cannot
/// change the answer.
/// </summary>
[TestFixture]
public class LdapExchangeCheckFailuresTests
{
    private const uint KerberosErrorBase = 0x96C73A00;
    private static readonly LdapExchangeKerberosSignIn SignIn = new("svc-jim@PANOPLY.LOCAL", "PANOPLY.LOCAL", "dc1.panoply.local");
    private static readonly Uri Endpoint = new("http://ex1.panoply.local/PowerShell/");

    [Test]
    public void Describe_ClockSkew_SaysTheClocksDifferByMoreThanFiveMinutes()
    {
        var message = Describe(Kerberos(37));

        Assert.That(message, Does.Contain("clock").And.Contain("five minutes").And.Contain("dc1.panoply.local"));
    }

    [TestCase(24u)]
    [TestCase(31u)]
    public void Describe_WrongPassword_SaysTheDomainRefusedThePasswordForTheAccount(uint code)
    {
        Assert.That(Describe(Kerberos(code)), Does.Contain("password").And.Contain("svc-jim@PANOPLY.LOCAL"));
    }

    [Test]
    public void Describe_UnknownServicePrincipal_NamesTheServicePrincipalTheDomainLacks()
    {
        Assert.That(Describe(Kerberos(7)), Does.Contain("HTTP/ex1.panoply.local"));
    }

    [Test]
    public void Describe_UnknownClient_SaysTheDomainDoesNotKnowTheAccount()
    {
        Assert.That(Describe(Kerberos(6)), Does.Contain("svc-jim@PANOPLY.LOCAL").And.Contain("does not know"));
    }

    [Test]
    public void Describe_AccountDisabledOrLocked_SaysSo()
    {
        Assert.That(Describe(Kerberos(18)), Does.Contain("disabled or locked out"));
    }

    [Test]
    public void Describe_PasswordExpired_SaysSo()
    {
        Assert.That(Describe(Kerberos(23)), Does.Contain("password has expired"));
    }

    [Test]
    public void Describe_DomainControllerUnreachable_NamesItAndThePort()
    {
        Assert.That(Describe(Kerberos(0x9C)), Does.Contain("dc1.panoply.local").And.Contain("88"));
    }

    [Test]
    public void Describe_AnyOtherKerberosRefusal_KeepsKerberosOwnWords()
    {
        var error = new KerberosException("gss_init_sec_context", 0xD0000, KerberosErrorBase + 60, "Generic error (see e-text)");

        Assert.That(Describe(error), Does.Contain("Kerberos").And.Contain("Generic error (see e-text)"));
    }

    [Test]
    public void Describe_KerberosRefusalWrappedByTheTransport_IsStillReadFromItsCode()
    {
        var wrapped = new InvalidOperationException("Connecting to the remote server failed.", Kerberos(37));

        Assert.That(Describe(wrapped), Does.Contain("five minutes"));
    }

    [Test]
    public void Describe_ExchangeRefusedTheAccount_SaysTheSignInWorkedButExchangeRefusedItWithItsOwnWords()
    {
        var error = new WSManFaultException(5, "AccessDenied", "The user \"PANOPLY\\svc-jim\" isn't assigned to any management roles.");

        var message = Describe(error);

        Assert.That(message, Does.Contain("Exchange refused svc-jim@PANOPLY.LOCAL").And.Contain("isn't assigned to any management roles").And.Contain("Recipient Management"));
    }

    [Test]
    public void Describe_ServerUnreachable_NamesTheEndpoint()
    {
        var error = new HttpRequestException("Connection refused (ex1.panoply.local:80)", new SocketException((int)SocketError.ConnectionRefused));

        Assert.That(Describe(error), Does.Contain("could not reach Exchange at http://ex1.panoply.local/PowerShell/"));
    }

    [Test]
    public void Describe_TransportWordsWithoutAFullStop_AreEndedBeforeJimsAdvice()
    {
        // The transport's own words rarely end in a full stop ("Connection refused"); run on into the advice that
        // follows, they read as one garbled sentence.
        var error = new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));

        Assert.That(Describe(error), Does.Contain("Connection refused. Check"));
    }

    [Test]
    public void Describe_TransportWordsEndingInAFullStop_AreNotGivenASecond()
    {
        var error = new HttpRequestException("Connection refused.", new SocketException((int)SocketError.ConnectionRefused));

        Assert.That(Describe(error), Does.Not.Contain(".."));
    }

    [Test]
    public void Describe_ServerDidNotAnswer_SaysSo()
    {
        Assert.That(Describe(new TimeoutException("The request timed out.")), Does.Contain("did not answer"));
    }

    [Test]
    public void Describe_CertificateNotTrusted_PointsAtCertificates()
    {
        var error = new HttpRequestException("The SSL connection could not be established.", new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot"));

        Assert.That(Describe(error), Does.Contain("certificate").And.Contain("Certificates").And.Contain("UntrustedRoot"));
    }

    [Test]
    public void Describe_AnythingElse_KeepsTheTransportsOwnWords()
    {
        var error = new WSManFaultException(2150858770, "InvalidSelectors", "The WS-Management service cannot process the request.");

        Assert.That(Describe(error), Does.Contain("The WS-Management service cannot process the request."));
    }

    private static string Describe(Exception error) => LdapExchangeCheckFailures.Describe(error, SignIn, Endpoint);

    private static KerberosException Kerberos(uint code) =>
        new("gss_init_sec_context", 0xD0000, KerberosErrorBase + code, "Kerberos's own description");
}
