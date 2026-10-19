// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using JIM.TestSupport;
using JIM.Web.Authentication;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The connection JIM makes to its identity provider (discovery, signing keys, token redemption), over a real TLS
/// handshake against a server whose certificate comes from a private certificate authority no operating system
/// trusts (#2363). Only the JIM certificate store's sign-in certificates can vouch for it.
/// </summary>
[TestFixture]
public class IdentityProviderBackchannelTests
{
    private const string Discovery = """{"issuer":"https://localhost/realms/jim"}""";

    private X509Certificate2 _root = null!;
    private X509Certificate2 _issuingCa = null!;
    private X509Certificate2 _server = null!;
    private TlsTestServer _identityProvider = null!;
    private List<X509Certificate2> _signInCertificates = null!;
    private SignInCertificateSnapshot _snapshot = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _root = TestPki.CreateRoot("Contoso Root CA");
        _issuingCa = TestPki.CreateIntermediate("Contoso Issuing CA 01", _root);
        _server = TestPki.CreateServer("localhost", _issuingCa);
        _identityProvider = TlsTestServer.Start(_server, _ => Discovery, TestPki.PublicOnly(_issuingCa));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        await _identityProvider.DisposeAsync();
        _server.Dispose();
        _issuingCa.Dispose();
        _root.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _signInCertificates = [];

        // Always stale, so every request sees the store as it is now: these tests are about the handshake, not the
        // snapshot's freshness window.
        _snapshot = new SignInCertificateSnapshot(_ => Task.FromResult<IReadOnlyCollection<X509Certificate2>>(_signInCertificates.ToArray()),
            TimeProvider.System, freshFor: TimeSpan.Zero);
    }

    [Test]
    public void SendAsync_NothingVouchesForTheCertificateAuthority_Refused()
    {
        using var client = NewClient();

        Assert.That(async () => await client.GetStringAsync(_identityProvider.Address()), Throws.InstanceOf<HttpRequestException>());
    }

    [Test]
    public async Task SendAsync_RootTrustedForSignIn_ConnectsAsync()
    {
        _signInCertificates.Add(TestPki.PublicOnly(_root));
        using var client = NewClient();

        var body = await client.GetStringAsync(_identityProvider.Address());

        Assert.That(body, Is.EqualTo(Discovery));
    }

    [Test]
    public async Task SendAsync_IssuingCaTrustedForSignIn_ConnectsAsync()
    {
        _signInCertificates.Add(TestPki.PublicOnly(_issuingCa));
        using var client = NewClient();

        var body = await client.GetStringAsync(_identityProvider.Address());

        Assert.That(body, Is.EqualTo(Discovery));
    }

    [Test]
    public void SendAsync_HostNameTheCertificateDoesNotCarry_RefusedEvenThoughTheStoreVouches()
    {
        _signInCertificates.Add(TestPki.PublicOnly(_root));
        using var client = NewClient();

        Assert.That(async () => await client.GetStringAsync(_identityProvider.Address("127.0.0.1")), Throws.InstanceOf<HttpRequestException>());
    }

    [Test]
    public async Task SendAsync_CertificateTrustedAfterARefusal_ConnectsOnTheSameClientWithoutARestartAsync()
    {
        using var client = NewClient();
        Assert.That(async () => await client.GetStringAsync(_identityProvider.Address()), Throws.InstanceOf<HttpRequestException>());

        _signInCertificates.Add(TestPki.PublicOnly(_root));
        var body = await client.GetStringAsync(_identityProvider.Address());

        Assert.That(body, Is.EqualTo(Discovery));
    }

    private HttpClient NewClient() => new(new IdentityProviderBackchannel(_snapshot).CreateHandler());
}
