// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using JIM.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="AccessTokenAuthenticationAudit"/>: an SSO access token that fails validation is recorded in
/// the security audit log, under a sanitised reason of its own (issue #2108).
/// </summary>
[TestFixture]
public class AccessTokenAuthenticationAuditTests
{
    private const string Issuer = "https://idp.example.com/realms/jim";
    private const string Audience = "jim-api";
    private const string ClientIp = "203.0.113.7";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("jim-access-token-test-signing-key-one")) { KeyId = "jim-test-key-1" };

    private static readonly SymmetricSecurityKey OtherKeyWithSameId =
        new(Encoding.UTF8.GetBytes("jim-access-token-test-signing-key-two")) { KeyId = "jim-test-key-1" };

    private static readonly SymmetricSecurityKey OtherKeyWithOtherId =
        new(Encoding.UTF8.GetBytes("jim-access-token-test-signing-key-two")) { KeyId = "jim-test-key-2" };

    // ─── CategoriseFailure ───

    private static IEnumerable<TestCaseData> FailureCases()
    {
        yield return new TestCaseData(new SecurityTokenExpiredException(), "Access token expired");
        yield return new TestCaseData(new SecurityTokenNotYetValidException(), "Access token not yet valid");
        yield return new TestCaseData(new SecurityTokenNoExpirationException(), "Access token lifetime invalid");
        yield return new TestCaseData(new SecurityTokenInvalidLifetimeException(), "Access token lifetime invalid");
        yield return new TestCaseData(new SecurityTokenSignatureKeyNotFoundException(), "Access token signing key not found");
        yield return new TestCaseData(new SecurityTokenInvalidSignatureException(), "Access token signature invalid");
        yield return new TestCaseData(new SecurityTokenInvalidSigningKeyException(), "Access token signing key invalid");
        yield return new TestCaseData(new SecurityTokenInvalidAlgorithmException(), "Access token algorithm not allowed");
        yield return new TestCaseData(new SecurityTokenInvalidIssuerException(), "Access token issuer invalid");
        yield return new TestCaseData(new SecurityTokenInvalidAudienceException(), "Access token audience invalid");
        yield return new TestCaseData(new SecurityTokenInvalidTypeException(), "Access token type invalid");
        yield return new TestCaseData(new SecurityTokenReplayDetectedException(), "Access token replayed");
        yield return new TestCaseData(new SecurityTokenMalformedException(), "Access token malformed");
        yield return new TestCaseData(new SecurityTokenArgumentException(), "Access token malformed");
        yield return new TestCaseData(new ArgumentException("IDX10209: Token has length: '300000' which is larger than the MaximumTokenSizeInBytes."), "Access token malformed");
        yield return new TestCaseData(new SecurityTokenDecryptionFailedException(), "Access token decryption failed");
        yield return new TestCaseData(new SecurityTokenValidationException(), "Access token validation failed");
        yield return new TestCaseData(new InvalidOperationException("IDX20803: Unable to obtain configuration"), "Access token validation failed");
        yield return new TestCaseData(null, "Access token validation failed");
    }

    [TestCaseSource(nameof(FailureCases))]
    public void CategoriseFailure_Exception_ReturnsItsReason(Exception? exception, string expected)
    {
        Assert.That(AccessTokenAuthenticationAudit.CategoriseFailure(exception), Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(FailureCases))]
    public void CategoriseFailure_AnyException_NeverSharesAReasonWithInteractiveSignIn(Exception? exception, string _)
    {
        // Failures aggregate on (prefix, IP, reason, window) and not on the event name, so a reason shared with the
        // OIDC sign-in categories ("OIDC ...") would merge access token failures into sign-in rows.
        Assert.That(AccessTokenAuthenticationAudit.CategoriseFailure(exception), Does.StartWith("Access token "));
    }

    [Test]
    public void CategoriseFailure_AggregateException_UsesItsFirstInnerException()
    {
        var exception = new AggregateException(new SecurityTokenExpiredException(), new SecurityTokenInvalidAudienceException());

        Assert.That(AccessTokenAuthenticationAudit.CategoriseFailure(exception), Is.EqualTo("Access token expired"));
    }

    [Test]
    public void CategoriseFailure_ExceptionMessage_NeverReachesTheReason()
    {
        var exception = new SecurityTokenMalformedException("IDX14100: JWT is not well formed: 'eyJhbGciOi.secret'");

        Assert.That(AccessTokenAuthenticationAudit.CategoriseFailure(exception), Does.Not.Contain("eyJ"));
    }

    // ─── OnAuthenticationFailedAsync ───

    [Test]
    public async Task OnAuthenticationFailedAsync_RecordsTheFailureWithItsReasonAndClientIpAsync()
    {
        var recorder = NewRecorder();
        var httpContext = NewHttpContext(recorder.Object);
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var context = new AuthenticationFailedContext(httpContext, scheme, new JwtBearerOptions())
        {
            Exception = new SecurityTokenExpiredException()
        };

        await AccessTokenAuthenticationAudit.OnAuthenticationFailedAsync(context);

        recorder.Verify(r => r.RecordAsync(
            FailedAuthenticationEventNames.AccessToken, "Access token expired", null, ClientIp), Times.Once);
    }

    [Test]
    public void OnAuthenticationFailedAsync_NoRecorderRegistered_FailsLoudly()
    {
        // A missing registration must surface at once rather than silently stop recording every failure.
        var httpContext = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var context = new AuthenticationFailedContext(httpContext, scheme, new JwtBearerOptions())
        {
            Exception = new SecurityTokenExpiredException()
        };

        Assert.That(() => AccessTokenAuthenticationAudit.OnAuthenticationFailedAsync(context), Throws.InvalidOperationException);
    }

    [TestCase(typeof(InvalidOperationException))]
    [TestCase(typeof(TimeoutException))]
    public async Task OnAuthenticationFailedAsync_ServerFaultRatherThanTokenFault_RecordsNothingAsync(Type exceptionType)
    {
        // JwtBearerHandler raises AuthenticationFailed for anything thrown while authenticating: a duplicate identity
        // in OnTokenValidated, or the identity provider's metadata being unreachable. Neither means the caller
        // presented a bad credential, so neither belongs in the security audit log as one.
        var recorder = NewRecorder();
        var httpContext = NewHttpContext(recorder.Object);
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var context = new AuthenticationFailedContext(httpContext, scheme, new JwtBearerOptions())
        {
            Exception = (Exception)Activator.CreateInstance(exceptionType, "IDX20803: Unable to obtain configuration")!
        };

        await AccessTokenAuthenticationAudit.OnAuthenticationFailedAsync(context);

        VerifyNothingRecorded(recorder);
    }

    // ─── IsTokenValidationFailure ───

    private static IEnumerable<TestCaseData> TokenFailureCases()
    {
        yield return new TestCaseData(new SecurityTokenExpiredException(), true);
        yield return new TestCaseData(new SecurityTokenMalformedException(), true);
        yield return new TestCaseData(new SecurityTokenSignatureKeyNotFoundException(), true);
        yield return new TestCaseData(new AggregateException(new SecurityTokenExpiredException(), new SecurityTokenInvalidAudienceException()), true);
        yield return new TestCaseData(new AggregateException(new SecurityTokenExpiredException(), new InvalidOperationException()), false);
        yield return new TestCaseData(new AggregateException(), false);
        yield return new TestCaseData(new InvalidOperationException("Sequence contains more than one element"), false);
        // IdentityModel reports an oversized token as a plain ArgumentException carrying its IDX code.
        yield return new TestCaseData(new ArgumentException("IDX10209: Token has length: '300000' which is larger than the MaximumTokenSizeInBytes."), true);
        // JIM's own code throwing an ArgumentException (or a subclass) while resolving the caller is a server fault.
        yield return new TestCaseData(new ArgumentException("Value does not fall within the expected range."), false);
        yield return new TestCaseData(new ArgumentNullException("principal", "IDX-like but not IdentityModel's"), false);
        yield return new TestCaseData(new HttpRequestException(), false);
        yield return new TestCaseData(null, false);
    }

    [TestCaseSource(nameof(TokenFailureCases))]
    public void IsTokenValidationFailure_Exception_SaysWhetherTheTokenWasAtFault(Exception? exception, bool expected)
    {
        Assert.That(AccessTokenAuthenticationAudit.IsTokenValidationFailure(exception), Is.EqualTo(expected));
    }

    // ─── Through the real JWT bearer handler ───
    // The reasons above are only as good as the exceptions the handler actually raises, so these drive JIM's event
    // through JwtBearerHandler itself with real tokens.

    [Test]
    public async Task JwtBearerHandler_TokenThatIsNotAJwt_RecordsMalformedAsync()
    {
        var (result, recorder) = await AuthenticateAsync("jim_xx_not_a_jwt_and_not_an_api_key");

        Assert.That(result.Succeeded, Is.False);
        VerifyRecorded(recorder, "Access token malformed");
    }

    [Test]
    public async Task JwtBearerHandler_ExpiredToken_RecordsExpiredAsync()
    {
        var token = CreateToken(expires: DateTime.UtcNow.AddHours(-1), notBefore: DateTime.UtcNow.AddHours(-2));

        var (result, recorder) = await AuthenticateAsync(token);

        Assert.That(result.Succeeded, Is.False);
        VerifyRecorded(recorder, "Access token expired");
    }

    [Test]
    public async Task JwtBearerHandler_TokenSignedWithTheWrongKey_RecordsSignatureInvalidAsync()
    {
        var token = CreateToken(key: OtherKeyWithSameId);

        var (result, recorder) = await AuthenticateAsync(token);

        Assert.That(result.Succeeded, Is.False);
        VerifyRecorded(recorder, "Access token signature invalid");
    }

    [Test]
    public async Task JwtBearerHandler_TokenSignedWithAnUnknownKey_RecordsSigningKeyNotFoundAsync()
    {
        var token = CreateToken(key: OtherKeyWithOtherId);

        var (result, recorder) = await AuthenticateAsync(token);

        Assert.That(result.Succeeded, Is.False);
        VerifyRecorded(recorder, "Access token signing key not found");
    }

    [Test]
    public async Task JwtBearerHandler_TokenForAnotherAudience_RecordsAudienceInvalidAsync()
    {
        var token = CreateToken(audience: "some-other-api");

        var (result, recorder) = await AuthenticateAsync(token);

        Assert.That(result.Succeeded, Is.False);
        VerifyRecorded(recorder, "Access token audience invalid");
    }

    [Test]
    public async Task JwtBearerHandler_TokenFromAnotherIssuer_RecordsIssuerInvalidAsync()
    {
        var token = CreateToken(issuer: "https://idp.example.com/realms/elsewhere");

        var (result, recorder) = await AuthenticateAsync(token);

        Assert.That(result.Succeeded, Is.False);
        VerifyRecorded(recorder, "Access token issuer invalid");
    }

    [Test]
    public async Task JwtBearerHandler_OversizedToken_RecordsMalformedAsync()
    {
        var (result, recorder) = await AuthenticateAsync(new string('a', 300_000));

        Assert.That(result.Succeeded, Is.False);
        VerifyRecorded(recorder, "Access token malformed");
    }

    [Test]
    public async Task JwtBearerHandler_TokenValidatedHandlerThrows_RecordsNothingAsync()
    {
        // A valid token whose identity JIM cannot resolve (here, two users holding the same SSO identifier) is a
        // server-side fault: the exception propagates, and the security audit log does not call it a refused credential.
        var (authenticate, recorder) = PrepareAuthentication(CreateToken(),
            _ => throw new InvalidOperationException("Sequence contains more than one element"));

        Assert.That(async () => await authenticate(), Throws.InvalidOperationException);
        VerifyNothingRecorded(recorder);
    }

    [Test]
    public async Task JwtBearerHandler_ValidToken_RecordsNothingAsync()
    {
        var (result, recorder) = await AuthenticateAsync(CreateToken());

        Assert.That(result.Succeeded, Is.True);
        VerifyNothingRecorded(recorder);
    }

    // ─── Helpers ───

    private static Mock<IFailedAuthenticationRecorder> NewRecorder()
    {
        var recorder = new Mock<IFailedAuthenticationRecorder>();
        recorder.Setup(r => r.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        return recorder;
    }

    private static DefaultHttpContext NewHttpContext(IFailedAuthenticationRecorder recorder)
    {
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(ClientIp);
        return httpContext;
    }

    private static void VerifyRecorded(Mock<IFailedAuthenticationRecorder> recorder, string reason) =>
        recorder.Verify(r => r.RecordAsync(FailedAuthenticationEventNames.AccessToken, reason, null, ClientIp), Times.Once);

    private static void VerifyNothingRecorded(Mock<IFailedAuthenticationRecorder> recorder) =>
        recorder.Verify(r => r.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);

    private static string CreateToken(
        SecurityKey? key = null, string issuer = Issuer, string audience = Audience,
        DateTime? expires = null, DateTime? notBefore = null)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            IssuedAt = notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.HmacSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static async Task<(AuthenticateResult Result, Mock<IFailedAuthenticationRecorder> Recorder)> AuthenticateAsync(string token)
    {
        var (authenticate, recorder) = PrepareAuthentication(token, _ => Task.CompletedTask);
        return (await authenticate(), recorder);
    }

    // Drives JwtBearerHandler with the events JIM's Program.cs builds (JimJwtBearerEvents.Create), so these tests also
    // prove the wiring: dropping OnAuthenticationFailed from the factory fails them.
    private static (Func<Task<AuthenticateResult>> Authenticate, Mock<IFailedAuthenticationRecorder> Recorder) PrepareAuthentication(
        string token, Func<TokenValidatedContext, Task> onTokenValidated)
    {
        var options = new JwtBearerOptions
        {
            TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = SigningKey
            },
            Events = JimJwtBearerEvents.Create(onTokenValidated, new BearerSigningKeyRecovery(TimeProvider.System))
        };
        var monitor = new Mock<IOptionsMonitor<JwtBearerOptions>>();
        monitor.Setup(m => m.Get(It.IsAny<string>())).Returns(options);

        var recorder = NewRecorder();
        var httpContext = NewHttpContext(recorder.Object);
        httpContext.Request.Path = "/api/v1/userinfo";
        httpContext.Request.Headers.Authorization = $"Bearer {token}";

        var handler = new JwtBearerHandler(monitor.Object, NullLoggerFactory.Instance, UrlEncoder.Default);
        return (async () =>
        {
            await handler.InitializeAsync(
                new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler)), httpContext);
            return await handler.AuthenticateAsync();
        }, recorder);
    }
}
