// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Security.Claims;
using JIM.Web.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Turning a sign-in failure into the setting involved, the value JIM expected and the value it received (#2443).
/// Only failures a setting explains are diagnosed: a person the identity provider refused, or a sign-in a retry
/// recovers, is not a problem with JIM's configuration. No diagnosis ever carries a secret or a claim's value.
/// </summary>
[TestFixture]
public class SignInProblemDiagnosisTests
{
    private const string ClientId = "jim-web";
    private const string Authority = "https://sso.example.com/realms/jim";

    [Test]
    public void DiagnosePortalFailure_IdTokenForAnotherAudience_NamesTheClientId()
    {
        var failure = new SecurityTokenInvalidAudienceException("IDX10214") { InvalidAudience = "another-client" };

        var report = SignInProblemDiagnosis.DiagnosePortalFailure(failure, PortalOptions());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report, Is.Not.Null);
            Assert.That(report!.Source, Is.EqualTo(SignInProblemSource.Portal));
            Assert.That(report.Setting, Is.EqualTo("JIM_SSO_CLIENT_ID"));
            Assert.That(report.Expected, Is.EqualTo(ClientId));
            Assert.That(report.Received, Is.EqualTo("another-client"));
        }
    }

    [Test]
    public void DiagnosePortalFailure_IdTokenFromAnotherIssuer_ListsTheIssuersJimAccepts()
    {
        var failure = new SecurityTokenInvalidIssuerException("IDX10205") { InvalidIssuer = "https://other.example.com/realms/jim" };

        var report = SignInProblemDiagnosis.DiagnosePortalFailure(failure, PortalOptions());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report!.Setting, Is.EqualTo("JIM_SSO_VALID_ISSUERS"));
            Assert.That(report.Expected, Is.EqualTo(Authority));
            Assert.That(report.Received, Is.EqualTo("https://other.example.com/realms/jim"));
            Assert.That(report.Fix, Does.Contain("JIM_SSO_AUTHORITY"));
        }
    }

    [Test]
    public void DiagnosePortalFailure_FailureWrappedInAnAggregate_Diagnosed()
    {
        var failure = new AggregateException(new SecurityTokenInvalidAudienceException("IDX10214") { InvalidAudience = "another-client" });

        var report = SignInProblemDiagnosis.DiagnosePortalFailure(failure, PortalOptions());

        Assert.That(report?.Setting, Is.EqualTo("JIM_SSO_CLIENT_ID"));
    }

    [Test]
    public void DiagnosePortalFailure_InvalidClient_NamesTheSecretButNeverHoldsIt()
    {
        var options = PortalOptions();
        options.ClientSecret = "s3cret-value";

        var report = SignInProblemDiagnosis.DiagnosePortalFailure(ProtocolError("invalid_client"), options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report!.Setting, Is.EqualTo("JIM_SSO_SECRET"));
            Assert.That(report.Expected, Does.Contain(ClientId));
            Assert.That(report.Received, Is.EqualTo("invalid_client"));
            Assert.That(report.Fix, Does.Contain("JIM_SSO_CLIENT_ID"));
            Assert.That(report.ToString(), Does.Not.Contain("s3cret-value"));
        }
    }

    [Test]
    public void DiagnosePortalFailure_UnauthorisedClient_NamesTheClientId()
    {
        var report = SignInProblemDiagnosis.DiagnosePortalFailure(ProtocolError("unauthorized_client"), PortalOptions());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report!.Setting, Is.EqualTo("JIM_SSO_CLIENT_ID"));
            Assert.That(report.Received, Is.EqualTo("unauthorized_client"));
        }
    }

    [TestCase("invalid_scope")]
    [TestCase("invalid_resource")]
    public void DiagnosePortalFailure_ScopeOrResourceRefused_NamesTheApiScopeAndWhatJimAskedFor(string error)
    {
        var options = PortalOptions();
        options.Scope.Add("api://jim");

        var report = SignInProblemDiagnosis.DiagnosePortalFailure(ProtocolError(error), options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report!.Setting, Is.EqualTo("JIM_SSO_API_SCOPE"));
            Assert.That(report.Expected, Does.Contain("api://jim"));
            Assert.That(report.Received, Is.EqualTo(error));
        }
    }

    [Test]
    public void DiagnosePortalFailure_ErrorCodeOnlyInTheMessage_Diagnosed()
    {
        var failure = new OpenIdConnectProtocolException(
            "Message contains error: 'invalid_client', error_description: 'Invalid client credentials', error_uri: 'error_uri is null'.");

        var report = SignInProblemDiagnosis.DiagnosePortalFailure(failure, PortalOptions());

        Assert.That(report?.Setting, Is.EqualTo("JIM_SSO_SECRET"));
    }

    [TestCase("access_denied")]
    [TestCase("invalid_grant")]
    [TestCase("server_error")]
    public void DiagnosePortalFailure_ErrorNoSettingExplains_NotDiagnosed(string error)
    {
        Assert.That(SignInProblemDiagnosis.DiagnosePortalFailure(ProtocolError(error), PortalOptions()), Is.Null);
    }

    [Test]
    public void DiagnosePortalFailure_CorrelationFailed_NotDiagnosed()
    {
        Assert.That(SignInProblemDiagnosis.DiagnosePortalFailure(new Exception("Correlation failed."), PortalOptions()), Is.Null);
    }

    [Test]
    public void DiagnosePortalFailure_ExpiredIdToken_NotDiagnosed()
    {
        Assert.That(SignInProblemDiagnosis.DiagnosePortalFailure(new SecurityTokenExpiredException("IDX10223"), PortalOptions()), Is.Null);
    }

    [Test]
    public void DiagnosePortalFailure_NoFailure_NotDiagnosed()
    {
        Assert.That(SignInProblemDiagnosis.DiagnosePortalFailure(null, PortalOptions()), Is.Null);
    }

    [Test]
    public void DiagnoseAccessToken_ForAnotherAudience_NamesTheApiScope()
    {
        var reports = SignInProblemDiagnosis.DiagnoseAccessToken(Token(Authority, "account", "jim-powershell"), ["api://jim"], [Authority]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reports, Has.Count.EqualTo(1));
            Assert.That(reports[0].Source, Is.EqualTo(SignInProblemSource.Api));
            Assert.That(reports[0].Setting, Is.EqualTo("JIM_SSO_API_SCOPE"));
            Assert.That(reports[0].Expected, Is.EqualTo("api://jim"));
            Assert.That(reports[0].Received, Is.EqualTo("account, jim-powershell"));
            Assert.That(reports[0].Fix, Does.Contain("JIM_SSO_CLIENT_ID"));
        }
    }

    [Test]
    public void DiagnoseAccessToken_WithNoAudience_SaysSo()
    {
        var reports = SignInProblemDiagnosis.DiagnoseAccessToken(Token(Authority), ["api://jim"], [Authority]);

        Assert.That(reports[0].Received, Is.EqualTo("(none)"));
    }

    [Test]
    public void DiagnoseAccessToken_FromAnotherIssuer_ListsTheIssuersJimAccepts()
    {
        var reports = SignInProblemDiagnosis.DiagnoseAccessToken(Token("http://adfs.example.com/adfs/services/trust", "api://jim"), ["api://jim"],
            [Authority, "https://sso.example.com/realms/other"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reports, Has.Count.EqualTo(1));
            Assert.That(reports[0].Setting, Is.EqualTo("JIM_SSO_VALID_ISSUERS"));
            Assert.That(reports[0].Expected, Is.EqualTo($"{Authority}, https://sso.example.com/realms/other"));
            Assert.That(reports[0].Received, Is.EqualTo("http://adfs.example.com/adfs/services/trust"));
        }
    }

    [Test]
    public void DiagnoseAccessToken_WrongAudienceAndIssuer_BothDiagnosed()
    {
        var reports = SignInProblemDiagnosis.DiagnoseAccessToken(Token("https://other.example.com", "account"), ["api://jim"], [Authority]);

        Assert.That(reports, Has.Count.EqualTo(2));
    }

    [Test]
    public void DiagnoseAccessToken_OneAudienceMatches_NotDiagnosed()
    {
        var reports = SignInProblemDiagnosis.DiagnoseAccessToken(Token(Authority, "account", "api://jim"), ["api://jim"], [Authority]);

        Assert.That(reports, Is.Empty);
    }

    [Test]
    public void MissingClaim_ListsTheClaimTypesPresentButNoValues()
    {
        Claim[] claims =
        [
            new("sub", "f81d4fae-7dec-11d0-a765"),
            new("name", "Joe Bloggs"),
            new("email", "joe@example.com"),
            new("name", "Joe Bloggs")
        ];

        var report = SignInProblemDiagnosis.MissingClaim(SignInProblemSource.Portal, "oid", claims);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.Setting, Is.EqualTo("JIM_SSO_CLAIM_TYPE"));
            Assert.That(report.Expected, Is.EqualTo("oid"));
            Assert.That(report.Received, Is.EqualTo("email, name, sub"));
            Assert.That(report.ToString(), Does.Not.Contain("Joe Bloggs").And.Not.Contain("f81d4fae").And.Not.Contain("joe@example.com"));
        }
    }

    [Test]
    public void MissingClaim_NoClaimsAtAll_SaysSo()
    {
        var report = SignInProblemDiagnosis.MissingClaim(SignInProblemSource.Api, "oid", []);

        Assert.That(report.Received, Is.EqualTo("(none)"));
    }

    private static OpenIdConnectOptions PortalOptions()
    {
        var options = new OpenIdConnectOptions { ClientId = ClientId, Authority = Authority };
        options.TokenValidationParameters.ValidIssuers = [Authority];
        return options;
    }

    private static OpenIdConnectProtocolException ProtocolError(string error)
    {
        // As the OpenID Connect handler raises one: the code in the message and in Data.
        var exception = new OpenIdConnectProtocolException($"Message contains error: '{error}', error_description: 'error_description is null', error_uri: 'error_uri is null'.");
        exception.Data["error"] = error;
        return exception;
    }

    private static JsonWebToken Token(string issuer, params string[] audiences)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Subject = new ClaimsIdentity([new Claim("sub", "diagnosis-test")]),
            Expires = DateTime.UtcNow.AddMinutes(5)
        };
        if (audiences.Length > 0)
            descriptor.Claims = new System.Collections.Generic.Dictionary<string, object> { ["aud"] = audiences.Length == 1 ? audiences[0] : audiences };
        return new JsonWebToken(new JsonWebTokenHandler().CreateToken(descriptor));
    }
}
