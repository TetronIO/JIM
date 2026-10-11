// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using JIM.Web.Middleware.Api;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="ApiUnauthorisedResponse"/>: the 401 an unauthenticated /api request receives in place of
/// the portal's sign-in redirect, including the <c>WWW-Authenticate</c> challenge RFC 9110 s15.5.2 requires.
/// </summary>
[TestFixture]
public class ApiUnauthorisedResponseTests
{
    [Test]
    public void BuildChallenge_NoCredentialPresented_OmitsTheErrorCode()
    {
        // RFC 6750 s3.1: a request that carried no authentication information gets no error code.
        var request = new DefaultHttpContext().Request;

        Assert.That(ApiUnauthorisedResponse.BuildChallenge(request), Is.EqualTo("Bearer realm=\"JIM\""));
    }

    [TestCase("Bearer jim_ak_0123456789abcdef")]
    [TestCase("Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.sig")]
    public void BuildChallenge_RejectedBearerCredential_AddsInvalidToken(string authorization)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = authorization;

        Assert.That(ApiUnauthorisedResponse.BuildChallenge(request), Is.EqualTo("Bearer realm=\"JIM\", error=\"invalid_token\""));
    }

    [Test]
    public void BuildChallenge_KeyInXApiKeyHeader_IsNotAPresentedCredential()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers["X-API-Key"] = "jim_ak_0123456789abcdef";

        Assert.That(ApiUnauthorisedResponse.BuildChallenge(request), Is.EqualTo("Bearer realm=\"JIM\""));
    }

    [Test]
    public async Task WriteAsync_WritesA401WithTheChallengeAndAJsonBodyAsync()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer jim_ak_0123456789abcdef";
        context.Response.Body = new MemoryStream();

        await ApiUnauthorisedResponse.WriteAsync(context);

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
            Assert.That(context.Response.Headers.WWWAuthenticate.ToString(), Is.EqualTo("Bearer realm=\"JIM\", error=\"invalid_token\""));
            Assert.That(context.Response.ContentType, Does.StartWith("application/json"));
            Assert.That(body.RootElement.GetProperty("error").GetString(), Is.EqualTo("Unauthorized"));
        }
    }
}
