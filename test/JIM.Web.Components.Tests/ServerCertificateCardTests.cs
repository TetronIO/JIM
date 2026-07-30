// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Connectors;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Components.Tests;

/// <summary>
/// Covers the certificate card's own logic: what it shows about a refused certificate, and the formatting that makes
/// it comparable by eye against the JIM certificate store (#1132).
/// </summary>
[TestFixture]
public class ServerCertificateCardTests : JimComponentTestContext
{
    private static ServerCertificateDiagnostic Diagnostic(
        ServerCertificateFailureReason reason = ServerCertificateFailureReason.NameMismatch,
        string host = "10.0.0.5",
        bool selfSigned = false)
    {
        return new ServerCertificateDiagnostic
        {
            Host = host,
            Port = 636,
            FailureReason = reason,
            Subject = "CN=dc01.corp.local, O=Corp, C=GB",
            Issuer = selfSigned ? "CN=dc01.corp.local, O=Corp, C=GB" : "CN=Corp Issuing CA, O=Corp",
            SubjectAlternativeNames = ["dc01.corp.local", "dc01"],
            ValidFrom = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidTo = new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            Thumbprint = "ABCDEF0123",
            SignatureAlgorithm = "sha256RSA",
            IsSelfSigned = selfSigned,
            Remediation = "Connect using a name the certificate carries."
        };
    }

    [Test]
    public void ServerCertificateCard_ShowsTheCommonNameRatherThanTheWholeDistinguishedName()
    {
        var cut = Render<ServerCertificateCard>(p => p.Add(c => c.Diagnostic, Diagnostic()));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("dc01.corp.local"));
            Assert.That(cut.Markup, Does.Not.Contain("CN=dc01.corp.local, O=Corp, C=GB"));
        });
    }

    [Test]
    public void ServerCertificateCard_ShowsWhichHostWasConnectedTo()
    {
        var cut = Render<ServerCertificateCard>(p => p.Add(c => c.Diagnostic, Diagnostic()));

        Assert.That(cut.Markup, Does.Contain("10.0.0.5:636"));
    }

    /// <summary>
    /// A thumbprint is compared by eye against the one in Admin &gt; Certificates, which is why it is grouped.
    /// </summary>
    [Test]
    public void ServerCertificateCard_GroupsTheThumbprintIntoPairs()
    {
        var cut = Render<ServerCertificateCard>(p => p.Add(c => c.Diagnostic, Diagnostic()));

        Assert.That(cut.Markup, Does.Contain("AB CD EF 01 23"));
    }

    [Test]
    public void ServerCertificateCard_MarksTheNameThatWouldHaveSatisfiedTheCheck()
    {
        var cut = Render<ServerCertificateCard>(p => p.Add(c => c.Diagnostic, Diagnostic(host: "dc01.corp.local")));

        Assert.That(cut.Markup, Does.Contain("jim-certificate-name-match"));
    }

    [Test]
    public void ServerCertificateCard_WhenNoNameMatches_MarksNone()
    {
        var cut = Render<ServerCertificateCard>(p => p.Add(c => c.Diagnostic, Diagnostic(host: "10.0.0.5")));

        Assert.That(cut.Markup, Does.Not.Contain("jim-certificate-name-match"));
    }

    [Test]
    public void ServerCertificateCard_WithASelfSignedCertificate_SaysSoRatherThanNamingItsOwnSubjectAsTheIssuer()
    {
        var cut = Render<ServerCertificateCard>(p => p.Add(c => c.Diagnostic, Diagnostic(selfSigned: true)));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Self-signed"));
            Assert.That(cut.Markup, Does.Not.Contain("Issued by"));
        });
    }

    [Test]
    public void ServerCertificateCard_NamesTheReasonTheCertificateWasRefused()
    {
        var cut = Render<ServerCertificateCard>(p => p.Add(c => c.Diagnostic, Diagnostic(ServerCertificateFailureReason.Expired)));

        Assert.That(cut.Markup, Does.Contain("Expired"));
    }

    [Test]
    public void ServerCertificateCard_ShowsTheRemediation()
    {
        var cut = Render<ServerCertificateCard>(p => p.Add(c => c.Diagnostic, Diagnostic()));

        Assert.That(cut.Markup, Does.Contain("Connect using a name the certificate carries."));
    }

    /// <summary>
    /// An expired certificate's dates are what the reader needs drawn to, so they carry the problem styling.
    /// </summary>
    [Test]
    public void ServerCertificateCard_WithAnExpiredCertificate_HighlightsTheValidityDates()
    {
        var expired = Diagnostic(ServerCertificateFailureReason.Expired);
        expired.ValidTo = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var cut = Render<ServerCertificateCard>(p => p.Add(c => c.Diagnostic, expired));

        Assert.That(cut.Markup, Does.Contain("jim-certificate-value-problem"));
    }

    [Test]
    public void ServerCertificateCard_WithNoDiagnostic_RendersNothing()
    {
        var cut = Render<ServerCertificateCard>(p => p.Add(c => c.Diagnostic, (ServerCertificateDiagnostic?)null));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }
}
