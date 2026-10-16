// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using JIM.Web.Extensions.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The security the published OpenAPI document declares. Asserted on the serialised document rather than the object
/// model, because a requirement whose scheme reference cannot be resolved is silently dropped at serialisation and
/// leaves an empty requirement behind, which OpenAPI reads as "no authentication needed".
/// </summary>
[TestFixture]
public class OpenApiSecurityConfigurationTests
{
    private const string ApiScope = "api://jim/access";

    [Test]
    public async Task Apply_ApiKeyScheme_IsAnHttpBearerSchemeAsync()
    {
        var json = await SerialiseAsync();

        var apiKey = json.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(apiKey.GetProperty("type").GetString(), Is.EqualTo("http"));
            Assert.That(apiKey.GetProperty("scheme").GetString(), Is.EqualTo("bearer"));
            Assert.That(apiKey.TryGetProperty("in", out _), Is.False, "a bearer scheme names no header of its own");
        }
    }

    [Test]
    public async Task Apply_SecurityRequirements_AreOneAlternativePerSchemeAsync()
    {
        var json = await SerialiseAsync();

        var requirements = json.RootElement.GetProperty("security").EnumerateArray().ToList();
        Assert.That(requirements, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requirements[0].EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "OAuth2" }));
            Assert.That(requirements[0].GetProperty("OAuth2").EnumerateArray().Select(s => s.GetString()),
                Is.EqualTo(new[] { "openid", ApiScope }));
            Assert.That(requirements[1].EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "ApiKey" }));
            Assert.That(requirements[1].GetProperty("ApiKey").GetArrayLength(), Is.Zero);
        }
    }

    [Test]
    public async Task ApplyToOperation_AnonymousEndpoint_PublishesThatItNeedsNoAuthenticationAsync()
    {
        // The health probes and the auth configuration bootstrap are [AllowAnonymous]. Without an operation-level
        // "security: []" they inherit the document-wide requirement, and a client generator or gateway that honours
        // the document demands a credential the endpoint never asks for.
        var json = await SerialiseWithOperationAsync(new List<object> { new AllowAnonymousAttribute() });

        var security = json.RootElement.GetProperty("paths").GetProperty("/probe").GetProperty("get").GetProperty("security");
        Assert.That(security.GetArrayLength(), Is.Zero);
    }

    [Test]
    public async Task ApplyToOperation_AuthenticatedEndpoint_InheritsTheDocumentRequirementAsync()
    {
        var json = await SerialiseWithOperationAsync(new List<object> { new AuthorizeAttribute() });

        var operation = json.RootElement.GetProperty("paths").GetProperty("/probe").GetProperty("get");
        Assert.That(operation.TryGetProperty("security", out _), Is.False);
    }

    private static async Task<JsonDocument> SerialiseWithOperationAsync(IList<object> endpointMetadata)
    {
        var document = new OpenApiDocument();
        OpenApiSecurityConfiguration.Apply(document, new Uri("https://idp.example.com/auth"),
            new Uri("https://idp.example.com/token"), ApiScope);
        var operation = new OpenApiOperation { Responses = new OpenApiResponses() };
        document.Paths = new OpenApiPaths
        {
            ["/probe"] = new OpenApiPathItem { Operations = new Dictionary<HttpMethod, OpenApiOperation> { [HttpMethod.Get] = operation } }
        };

        OpenApiSecurityConfiguration.ApplyToOperation(operation, endpointMetadata);

        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1);
        return JsonDocument.Parse(json);
    }

    private static async Task<JsonDocument> SerialiseAsync()
    {
        var document = new OpenApiDocument();
        OpenApiSecurityConfiguration.Apply(document, new Uri("https://idp.example.com/auth"),
            new Uri("https://idp.example.com/token"), ApiScope);

        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1);
        return JsonDocument.Parse(json);
    }
}
