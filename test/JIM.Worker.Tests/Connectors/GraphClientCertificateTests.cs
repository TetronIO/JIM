// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Graph;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class GraphClientCertificateTests
{
    [Test]
    public void Load_ValidRsaPfx_ReturnsCertificateWithPrivateKey()
    {
        using var result = GraphClientCertificate.Load(GraphTestCertificates.RsaPfx(), GraphTestCertificates.Password, DateTime.UtcNow);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Warning, Is.Null);
            Assert.That(result.Certificate, Is.Not.Null);
            Assert.That(result.Certificate!.HasPrivateKey, Is.True);
        }
    }

    [Test]
    public void Load_PfxWithNoPassword_LoadsWithoutOne()
    {
        using var result = GraphClientCertificate.Load(GraphTestCertificates.RsaPfx(password: null), password: null, DateTime.UtcNow);

        Assert.That(result.Certificate, Is.Not.Null, result.Error);
    }

    [Test]
    public void Load_SurroundingWhitespace_IsIgnored()
    {
        using var result = GraphClientCertificate.Load($"  {GraphTestCertificates.RsaPfx()}\n", GraphTestCertificates.Password, DateTime.UtcNow);

        Assert.That(result.Certificate, Is.Not.Null, result.Error);
    }

    [Test]
    public void Load_NotBase64_FailsSayingSo()
    {
        using var result = GraphClientCertificate.Load("not base64 at all!", GraphTestCertificates.Password, DateTime.UtcNow);

        AssertFailedMentioning(result, "Base64");
    }

    [Test]
    public void Load_Base64ThatIsNotAPfx_FailsNamingPasswordOrFormat()
    {
        using var result = GraphClientCertificate.Load(Convert.ToBase64String("hello"u8.ToArray()), GraphTestCertificates.Password, DateTime.UtcNow);

        AssertFailedMentioning(result, "PFX");
    }

    [Test]
    public void Load_WrongPassword_FailsNamingThePasswordSetting()
    {
        using var result = GraphClientCertificate.Load(GraphTestCertificates.RsaPfx(), "wrong-password", DateTime.UtcNow);

        AssertFailedMentioning(result, GraphConnectorConstants.SettingCertificatePassword);
    }

    [Test]
    public void Load_WrongPassword_ErrorNeverContainsThePassword()
    {
        using var result = GraphClientCertificate.Load(GraphTestCertificates.RsaPfx(), "wrong-password", DateTime.UtcNow);

        Assert.That(result.Error, Does.Not.Contain("wrong-password"));
    }

    [Test]
    public void Load_NoPrivateKey_FailsSayingTheKeyIsMissing()
    {
        using var result = GraphClientCertificate.Load(GraphTestCertificates.PfxWithoutPrivateKey(), GraphTestCertificates.Password, DateTime.UtcNow);

        AssertFailedMentioning(result, "private key");
    }

    [Test]
    public void Load_EcdsaKey_FailsSayingRsaIsRequired()
    {
        using var result = GraphClientCertificate.Load(GraphTestCertificates.EcdsaPfx(), GraphTestCertificates.Password, DateTime.UtcNow);

        AssertFailedMentioning(result, "RSA");
    }

    [Test]
    public void Load_RsaKeyBelowMinimumSize_FailsNamingTheMinimum()
    {
        using var result = GraphClientCertificate.Load(GraphTestCertificates.RsaPfx(keySize: 1024), GraphTestCertificates.Password, DateTime.UtcNow);

        AssertFailedMentioning(result, GraphConnectorConstants.MinimumRsaKeySizeBits.ToString());
    }

    [Test]
    public void Load_Expired_FailsSayingItHasExpired()
    {
        var pfx = GraphTestCertificates.RsaPfx(notBefore: DateTimeOffset.UtcNow.AddYears(-2), notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        using var result = GraphClientCertificate.Load(pfx, GraphTestCertificates.Password, DateTime.UtcNow);

        AssertFailedMentioning(result, "expired");
    }

    [Test]
    public void Load_NotYetValid_FailsSayingWhenItBecomesValid()
    {
        var pfx = GraphTestCertificates.RsaPfx(notBefore: DateTimeOffset.UtcNow.AddDays(2), notAfter: DateTimeOffset.UtcNow.AddYears(1));

        using var result = GraphClientCertificate.Load(pfx, GraphTestCertificates.Password, DateTime.UtcNow);

        AssertFailedMentioning(result, "not valid until");
    }

    [Test]
    public void Load_ExpiresWithinWarningPeriod_LoadsWithAWarning()
    {
        var pfx = GraphTestCertificates.RsaPfx(notAfter: DateTimeOffset.UtcNow.AddDays(GraphConnectorConstants.CertificateExpiryWarningDays - 5));

        using var result = GraphClientCertificate.Load(pfx, GraphTestCertificates.Password, DateTime.UtcNow);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Certificate, Is.Not.Null, result.Error);
            Assert.That(result.Warning, Does.Contain("expires"));
        }
    }

    private static void AssertFailedMentioning(GraphClientCertificateLoadResult result, string expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Certificate, Is.Null);
            Assert.That(result.Error, Does.Contain(expected).IgnoreCase);
        }
    }
}
