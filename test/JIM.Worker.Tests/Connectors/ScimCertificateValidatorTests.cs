// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using JIM.Connectors.SCIM;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// TLS trust for SCIM endpoints: the system CA store is consulted first, then JIM's own trusted
/// certificate store, which lets a deployment trust an internal CA without installing it on the host.
/// <para>
/// This validator is deliberately stricter than the LDAP connector's equivalent. JIM's store answers
/// "do we trust the issuer", so it waives an unknown authority only. Certificate expiry and hostname
/// mismatches are never waived; the latter in particular is an interception signal rather than a
/// trust-configuration gap.
/// </para>
/// </summary>
[TestFixture]
public class ScimCertificateValidatorTests
{
    private ILogger _logger = null!;
    private readonly List<X509Certificate2> _disposables = [];

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
        foreach (var certificate in _disposables)
            certificate.Dispose();
        _disposables.Clear();
    }

    #region certificate fixtures

    private X509Certificate2 CreateCertificateAuthority(string name = "CN=JIM Test Internal CA")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));

        // A wide window so a deliberately expired leaf can still sit inside its issuer's validity period;
        // signing rejects a leaf that starts before its issuer does.
        var authority = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-400), DateTimeOffset.UtcNow.AddDays(365));
        _disposables.Add(authority);
        return authority;
    }

    private X509Certificate2 CreateSignedCertificate(
        X509Certificate2 authority,
        string subject = "CN=scim.example.com",
        int notBeforeDays = -1,
        int notAfterDays = 30)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        var signed = request.Create(authority, DateTimeOffset.UtcNow.AddDays(notBeforeDays), DateTimeOffset.UtcNow.AddDays(notAfterDays), serial);
        _disposables.Add(signed);
        return signed;
    }

    private X509Certificate2 CreateSelfSignedCertificate(string subject = "CN=scim.example.com")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        _disposables.Add(certificate);
        return certificate;
    }

    #endregion

    [Test]
    public void Validate_SystemStoreAlreadyTrustsTheCertificate_AcceptsWithoutConsultingJimStore()
    {
        var validator = new ScimCertificateValidator([], _logger);
        var certificate = CreateSelfSignedCertificate();

        var result = validator.Validate(certificate, chain: null, SslPolicyErrors.None);

        Assert.That(result, Is.True, "no SSL policy errors means the platform already validated the chain.");
    }

    [Test]
    public void Validate_IssuingAuthorityInJimStore_AcceptsTheLeafCertificate()
    {
        // The realistic internal-PKI case: JIM holds the private CA, the provider presents a leaf.
        var authority = CreateCertificateAuthority();
        var leaf = CreateSignedCertificate(authority);
        var validator = new ScimCertificateValidator([authority], _logger);

        var result = validator.Validate(leaf, chain: null, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.That(result, Is.True);
    }

    [Test]
    public void Validate_SelfSignedCertificateItselfInJimStore_Accepts()
    {
        // Equally common: no CA at all, and the administrator uploads the endpoint's own certificate.
        var certificate = CreateSelfSignedCertificate();
        var validator = new ScimCertificateValidator([certificate], _logger);

        var result = validator.Validate(certificate, chain: null, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.That(result, Is.True);
    }

    [Test]
    public void Validate_UntrustedIssuer_Rejects()
    {
        var untrustedAuthority = CreateCertificateAuthority("CN=Somebody Else CA");
        var leaf = CreateSignedCertificate(untrustedAuthority);
        var unrelatedAuthority = CreateCertificateAuthority("CN=JIM Test Internal CA");
        var validator = new ScimCertificateValidator([unrelatedAuthority], _logger);

        var result = validator.Validate(leaf, chain: null, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.That(result, Is.False);
    }

    [Test]
    public void Validate_EmptyJimStore_RejectsChainError()
    {
        var certificate = CreateSelfSignedCertificate();
        var validator = new ScimCertificateValidator([], _logger);

        var result = validator.Validate(certificate, chain: null, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.That(result, Is.False);
    }

    [Test]
    public void Validate_HostnameMismatch_RejectsEvenWhenIssuerIsTrusted()
    {
        // Trusting an issuer says nothing about which host may present its certificates. Waiving this
        // would accept any host holding a certificate from the same internal CA.
        var authority = CreateCertificateAuthority();
        var leaf = CreateSignedCertificate(authority);
        var validator = new ScimCertificateValidator([authority], _logger);

        var result = validator.Validate(leaf, chain: null, SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.That(result, Is.False);
    }

    [Test]
    public void Validate_ExpiredCertificate_RejectsEvenWhenIssuerIsTrusted()
    {
        var authority = CreateCertificateAuthority();
        var expiredLeaf = CreateSignedCertificate(authority, notBeforeDays: -60, notAfterDays: -30);
        var validator = new ScimCertificateValidator([authority], _logger);

        var result = validator.Validate(expiredLeaf, chain: null, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.That(result, Is.False, "an expired certificate is not a trust-configuration gap.");
    }

    [Test]
    public void Validate_NoCertificatePresented_Rejects()
    {
        var authority = CreateCertificateAuthority();
        var validator = new ScimCertificateValidator([authority], _logger);

        var result = validator.Validate(certificate: null, chain: null, SslPolicyErrors.RemoteCertificateNotAvailable);

        Assert.That(result, Is.False);
    }

    [Test]
    public void Validate_ChainErrorCombinedWithNameMismatch_Rejects()
    {
        var authority = CreateCertificateAuthority();
        var leaf = CreateSignedCertificate(authority);
        var validator = new ScimCertificateValidator([authority], _logger);

        var result = validator.Validate(leaf, chain: null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.That(result, Is.False, "the waivable error must be the only error present.");
    }
}
