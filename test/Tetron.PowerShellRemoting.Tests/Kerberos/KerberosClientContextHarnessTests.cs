// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using Tetron.PowerShellRemoting.Encryption;
using Tetron.PowerShellRemoting.Kerberos;

namespace Tetron.PowerShellRemoting.Tests.Kerberos;

/// <summary>Against the harness's real KDC (skipped without it; see <see cref="KerberosHarness"/>).</summary>
[Category("RequiresRemotingHarness")]
public class KerberosClientContextHarnessTests
{
    [SetUp]
    public void SetUp() => KerberosHarness.RequireHarness();

    [Test]
    public void Step_AgainstTheService_EstablishesMutuallyAuthenticatedContext()
    {
        using var client = KerberosClientContext.Create(KerberosHarness.ClientPrincipal, KerberosHarness.ClientPassword, KerberosHarness.ServiceHost);
        using var service = new TestKerberosAcceptor();

        Establish(client, service);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(client.IsEstablished, Is.True);
            Assert.That(service.IsEstablished, Is.True);
        }
    }

    [Test]
    public void Wrap_ThenServiceUnwraps_ReturnsTheOriginalMessage()
    {
        using var client = KerberosClientContext.Create(KerberosHarness.ClientPrincipal, KerberosHarness.ClientPassword, KerberosHarness.ServiceHost);
        using var service = new TestKerberosAcceptor();
        Establish(client, service);
        var message = Encoding.UTF8.GetBytes("<s:Envelope>" + new string('x', 20_000) + "</s:Envelope>");

        var sealedMessage = client.Wrap(message);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sealedMessage.Data, Is.Not.EqualTo(message), "The data must be encrypted, not just signed.");
            Assert.That(sealedMessage.PaddingLength, Is.Zero, "AES uses no padding.");
            Assert.That(service.Unwrap(sealedMessage.Header, sealedMessage.Data), Is.EqualTo(message));
        }
    }

    [Test]
    public void ServiceWrap_ThenClientUnwraps_ReturnsTheOriginalMessage()
    {
        using var client = KerberosClientContext.Create(KerberosHarness.ClientPrincipal, KerberosHarness.ClientPassword, KerberosHarness.ServiceHost);
        using var service = new TestKerberosAcceptor();
        Establish(client, service);
        var message = "<s:Envelope>reply</s:Envelope>"u8.ToArray();

        var sealedMessage = service.Wrap(message);

        Assert.That(client.Unwrap(sealedMessage.Header, sealedMessage.Data), Is.EqualTo(message));
    }

    [Test]
    public void EncodeThenDecode_ThroughTheWinRMFraming_RoundTripsBothWays()
    {
        using var client = KerberosClientContext.Create(KerberosHarness.ClientPrincipal, KerberosHarness.ClientPassword, KerberosHarness.ServiceHost);
        using var service = new TestKerberosAcceptor();
        Establish(client, service);
        var request = "<s:Envelope>request</s:Envelope>"u8.ToArray();

        var encoded = WinRMEncryptedMessage.Encode(request, client);
        var received = WinRMEncryptedMessage.Decode(encoded.Body, encoded.ContentType, service);

        Assert.That(received, Is.EqualTo(request));
    }

    [Test]
    public void Unwrap_TamperedData_IsRefused()
    {
        using var client = KerberosClientContext.Create(KerberosHarness.ClientPrincipal, KerberosHarness.ClientPassword, KerberosHarness.ServiceHost);
        using var service = new TestKerberosAcceptor();
        Establish(client, service);
        var sealedMessage = client.Wrap("<s:Envelope>request</s:Envelope>"u8);
        sealedMessage.Data[0] ^= 0xFF;

        Assert.That(() => service.Unwrap(sealedMessage.Header, sealedMessage.Data), Throws.TypeOf<WinRMEncryptionException>());
    }

    [Test]
    public void Create_WrongPassword_FailsWithKerberosOwnExplanation()
    {
        Assert.That(() => KerberosClientContext.Create(KerberosHarness.ClientPrincipal, "not-the-password", KerberosHarness.ServiceHost),
            Throws.TypeOf<KerberosException>()
                .With.Property(nameof(KerberosException.Operation)).EqualTo("gss_acquire_cred_with_password")
                .And.Message.Not.Contains("not-the-password"));
    }

    [Test]
    public void Step_ServiceWithNoPrincipal_FailsNamingTheProblem()
    {
        using var client = KerberosClientContext.Create(KerberosHarness.ClientPrincipal, KerberosHarness.ClientPassword, "no-such-host.harness.test");

        Assert.That(() => client.Step(ReadOnlySpan<byte>.Empty),
            Throws.TypeOf<KerberosException>().With.Property(nameof(KerberosException.Operation)).EqualTo("gss_init_sec_context"));
    }

    private static void Establish(KerberosClientContext client, TestKerberosAcceptor service)
    {
        var token = client.Step(ReadOnlySpan<byte>.Empty);
        for (var round = 0; round < 5 && token is not null; round++)
        {
            var reply = service.Accept(token);
            token = reply is null || client.IsEstablished ? null : client.Step(reply);
        }
    }
}
