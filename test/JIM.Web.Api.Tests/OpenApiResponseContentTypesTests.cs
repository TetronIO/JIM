// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The content types the published OpenAPI document gives each response. A controller-wide
/// <see cref="ProducesAttribute"/> is merged into every response, including one whose action declares its own type
/// (a certificate download), which would publish that response as JSON too.
/// </summary>
[TestFixture]
public class OpenApiResponseContentTypesTests
{
    private const string CertificateContentType = "application/x-x509-ca-cert";

    [Test]
    public void ApplyToOperation_ResponseDeclaringItsOwnContentType_PublishesOnlyThatType()
    {
        var operation = CertificateDownloadOperation();

        OpenApiResponseContentTypes.ApplyToOperation(operation, CertificateDownloadMetadata());

        Assert.That(operation.Responses!["200"].Content!.Keys, Is.EquivalentTo(new[] { CertificateContentType }));
    }

    [Test]
    public void ApplyToOperation_ResponseDeclaringNoContentType_KeepsTheControllerContentType()
    {
        var operation = CertificateDownloadOperation();

        OpenApiResponseContentTypes.ApplyToOperation(operation, CertificateDownloadMetadata());

        Assert.That(operation.Responses!["404"].Content!.Keys, Is.EquivalentTo(new[] { "application/json" }));
    }

    /// <summary>
    /// The download endpoint's metadata, in the order MVC builds it: the controller's attributes, then the action's.
    /// </summary>
    private static List<object> CertificateDownloadMetadata() =>
    [
        new ProducesAttribute("application/json"),
        new ProducesResponseTypeAttribute(typeof(FileContentResult), StatusCodes.Status200OK, CertificateContentType),
        new ProducesResponseTypeAttribute(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)
    ];

    /// <summary>
    /// The operation as the generator emits it before the transformer runs.
    /// </summary>
    private static OpenApiOperation CertificateDownloadOperation() => new()
    {
        Responses = new OpenApiResponses
        {
            ["200"] = new OpenApiResponse
            {
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    [CertificateContentType] = new(),
                    ["application/json"] = new()
                }
            },
            ["404"] = new OpenApiResponse
            {
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() }
            }
        }
    };
}
