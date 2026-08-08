// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Connectors;
using JIM.Models.Exceptions;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers the structured failure detail recorded on an Activity, which is what lets the portal show the certificate a
/// directory server presented rather than just the sentence describing the failure (#1132).
/// </summary>
[TestFixture]
public class ActivityErrorDetailTests
{
    private static ServerCertificateDiagnostic SampleDiagnostic()
    {
        return new ServerCertificateDiagnostic
        {
            Host = "dc01.corp.local",
            Port = 636,
            FailureReason = ServerCertificateFailureReason.NameMismatch,
            Subject = "CN=dc02.corp.local",
            Issuer = "CN=Corp Issuing CA",
            SubjectAlternativeNames = ["dc02.corp.local", "10.0.0.5"],
            ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidTo = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Thumbprint = "ABCDEF0123456789",
            SignatureAlgorithm = "sha256RSA",
            IsSelfSigned = false,
            Remediation = "Connect using a name the certificate carries."
        };
    }

    [Test]
    public void TryDescribe_WithARefusedCertificate_RoundTripsEveryFieldTheCardShows()
    {
        var expected = SampleDiagnostic();

        var detail = ActivityErrorDetail.TryDescribe(new ServerCertificateRejectedException("refused", expected));
        var actual = ActivityErrorDetail.TryReadServerCertificate(detail);

        Assert.That(actual, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual!.Host, Is.EqualTo(expected.Host));
            Assert.That(actual.Port, Is.EqualTo(expected.Port));
            Assert.That(actual.FailureReason, Is.EqualTo(expected.FailureReason));
            Assert.That(actual.Subject, Is.EqualTo(expected.Subject));
            Assert.That(actual.Issuer, Is.EqualTo(expected.Issuer));
            Assert.That(actual.SubjectAlternativeNames, Is.EqualTo(expected.SubjectAlternativeNames));
            Assert.That(actual.ValidFrom, Is.EqualTo(expected.ValidFrom));
            Assert.That(actual.ValidTo, Is.EqualTo(expected.ValidTo));
            Assert.That(actual.Thumbprint, Is.EqualTo(expected.Thumbprint));
            Assert.That(actual.SignatureAlgorithm, Is.EqualTo(expected.SignatureAlgorithm));
            Assert.That(actual.IsSelfSigned, Is.EqualTo(expected.IsSelfSigned));
            Assert.That(actual.Remediation, Is.EqualTo(expected.Remediation));
        }
    }

    /// <summary>
    /// The worker wraps connector failures before the Activity is failed, so the detail has to survive being buried.
    /// </summary>
    [Test]
    public void TryDescribe_WithARefusedCertificateInsideAnotherException_StillDescribesIt()
    {
        var rejection = new ServerCertificateRejectedException("refused", SampleDiagnostic());
        var wrapped = new InvalidOperationException("Import failed", new ApplicationException("connecting", rejection));

        var detail = ActivityErrorDetail.TryDescribe(wrapped);

        Assert.That(ActivityErrorDetail.TryReadServerCertificate(detail), Is.Not.Null);
    }

    [Test]
    public void TryDescribe_WithAnOrdinaryFailure_RecordsNothing()
    {
        Assert.That(ActivityErrorDetail.TryDescribe(new InvalidOperationException("something else")), Is.Null);
    }

    [Test]
    public void TryReadServerCertificate_WithNothingRecorded_ReturnsNull()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ActivityErrorDetail.TryReadServerCertificate(null), Is.Null);
            Assert.That(ActivityErrorDetail.TryReadServerCertificate(string.Empty), Is.Null);
        }
    }

    /// <summary>
    /// The column is deliberately open-ended, so a reader has to cope with content it does not recognise, or content
    /// written by a future version, without failing the page it is rendering.
    /// </summary>
    [Test]
    public void TryReadServerCertificate_WithUnrecognisedOrMalformedContent_ReturnsNullRatherThanThrowing()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ActivityErrorDetail.TryReadServerCertificate("{\"kind\":\"something-else\",\"payload\":42}"), Is.Null);
            Assert.That(ActivityErrorDetail.TryReadServerCertificate("not json at all"), Is.Null);
        }
    }
}
