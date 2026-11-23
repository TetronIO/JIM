// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using JIM.Connectors.Graph;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class EntraTokenFailureTests
{
    [TestCase(7000215, GraphConnectorConstants.SettingClientSecret)]
    [TestCase(7000222, "expired")]
    [TestCase(700016, GraphConnectorConstants.SettingClientId)]
    [TestCase(90002, GraphConnectorConstants.SettingCloud)]
    [TestCase(900023, GraphConnectorConstants.SettingTenantId)]
    [TestCase(700027, GraphConnectorConstants.SettingCertificate)]
    [TestCase(700024, "clock")]
    [TestCase(7000112, "disabled")]
    [TestCase(500011, GraphConnectorConstants.SettingCloud)]
    public void Describe_KnownErrorCode_NamesTheSettingOrCauseAndTheCode(int code, string expected)
    {
        var message = EntraTokenFailure.Describe(HttpStatusCode.BadRequest, $$"""{"error":"invalid_client","error_codes":[{{code}}]}""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain(expected).IgnoreCase);
            Assert.That(message, Does.Contain($"AADSTS{code}"));
        }
    }

    [Test]
    public void StillBeingAppliedRetryFor_Run_KeepsAskingForSeveralMinutes()
    {
        // Entra ID has been seen refusing a certificate for over a minute and a half after accepting it elsewhere; an
        // unattended run waits that out rather than failing.
        var total = EntraTokenFailure.StillBeingAppliedRetryFor(GraphClientPurpose.Run).Delays.Aggregate(TimeSpan.Zero, (sum, d) => sum + d);

        Assert.That(total, Is.GreaterThanOrEqualTo(TimeSpan.FromMinutes(2)));
    }

    [Test]
    public void StillBeingAppliedRetryFor_ConnectionTest_GivesUpWithinHalfAMinute()
    {
        // The connection test runs while an administrator waits on the form, so it reports the refusal sooner.
        var delays = EntraTokenFailure.StillBeingAppliedRetryFor(GraphClientPurpose.ConnectionTest).Delays;
        var total = delays.Aggregate(TimeSpan.Zero, (sum, d) => sum + d);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delays, Is.Not.Empty);
            Assert.That(total, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(30)));
        }
    }

    [Test]
    public void IsStillBeingApplied_CertificateNotYetKnown_IsTrue()
    {
        // Entra ID learns a newly uploaded certificate unevenly, so a token request signed with it can be refused for
        // some minutes after one has already succeeded.
        Assert.That(EntraTokenFailure.IsStillBeingApplied(HttpStatusCode.Unauthorized, """{"error":"invalid_client","error_codes":[700027]}"""), Is.True);
    }

    [TestCase("""{"error":"invalid_client","error_codes":[7000215]}""")]
    [TestCase("""{"error":"unauthorized_client","error_codes":[700016]}""")]
    [TestCase("""{"error":"invalid_request","error_codes":[123456]}""")]
    [TestCase("not json")]
    [TestCase(null)]
    public void IsStillBeingApplied_AnyOtherRefusal_IsFalse(string? body)
    {
        // A mistyped secret or client ID would only be retried into a longer wait for the same answer.
        Assert.That(EntraTokenFailure.IsStillBeingApplied(HttpStatusCode.Unauthorized, body), Is.False);
    }

    [Test]
    public void Describe_UnknownErrorCode_NamesTheCodeAndTheError()
    {
        var message = EntraTokenFailure.Describe(HttpStatusCode.BadRequest, """{"error":"invalid_request","error_codes":[123456]}""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("AADSTS123456"));
            Assert.That(message, Does.Contain("invalid_request"));
            Assert.That(message, Does.Contain("HTTP 400"));
        }
    }

    [Test]
    public void Describe_NeverIncludesTheErrorDescription()
    {
        // Entra ID's error_description carries trace and correlation ids and can echo request parameters.
        var message = EntraTokenFailure.Describe(HttpStatusCode.Unauthorized,
            """{"error":"invalid_client","error_description":"AADSTS7000215: secret-looking-text Trace ID: abc","error_codes":[7000215]}""");

        Assert.That(message, Does.Not.Contain("secret-looking-text"));
    }

    [Test]
    public void Describe_ErrorFieldThatIsNotAnOAuthErrorCode_IsLeftOut()
    {
        var message = EntraTokenFailure.Describe(HttpStatusCode.BadRequest, """{"error":"<script>alert(1)</script>"}""");

        Assert.That(message, Does.Not.Contain("script"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("<html>gateway error</html>")]
    [TestCase("[1,2,3]")]
    public void Describe_BodyThatIsNotAnEntraError_FallsBackToTheStatus(string? body)
    {
        var message = EntraTokenFailure.Describe(HttpStatusCode.BadGateway, body);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("HTTP 502"));
            Assert.That(message, Does.Not.Contain("gateway error"));
        }
    }
}
