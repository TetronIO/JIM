// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using JIM.Application.Exceptions;
using JIM.Models.Core;
using JIM.Web.Middleware.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Confirms the REST layer's mapping of <see cref="FeatureDisabledException"/> (thrown by
/// <c>FeatureFlagServer.EnsureEnabledAsync</c> when a caller reaches a feature-flag-gated entry point while the
/// flag is off, #242 Phase 3.5, #1781) to a clear, non-500 response naming the feature. No REST controller action
/// catches this exception itself (see <see cref="GlobalExceptionHandler"/>'s doc comment); it is the middleware
/// that turns it into an HTTP response, so that is what this test exercises directly.
/// </summary>
[TestFixture]
public class GlobalExceptionHandlerFeatureDisabledTests
{
    private static HttpContext BuildHttpContext(bool isDevelopment)
    {
        var services = new ServiceCollection();
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns(isDevelopment ? "Development" : "Production");
        services.AddSingleton(env.Object);

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() }
        };
        return context;
    }

    [Test]
    public async Task InvokeAsync_FeatureDisabledException_Returns400NamingTheFeatureAsync()
    {
        var definition = FeatureFlagCatalogue.UniqueValueGeneration;
        var handler = new GlobalExceptionHandler(
            _ => throw new FeatureDisabledException(definition),
            NullLogger<GlobalExceptionHandler>.Instance);
        var context = BuildHttpContext(isDevelopment: false);

        await handler.InvokeAsync(context);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest),
            "a disabled feature is a client error to correct (enable the flag, or don't use the feature), never a 500");

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.That(error, Is.Not.Null);
        Assert.That(error!.Code, Is.EqualTo(ApiErrorCodes.BadRequest));
        Assert.That(error.Message, Does.Contain(definition.DisplayName),
            "the error must name the disabled feature, not just say 'bad request'");
    }
}
