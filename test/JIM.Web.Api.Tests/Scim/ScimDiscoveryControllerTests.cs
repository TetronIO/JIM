// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Scim;
using JIM.Scim;
using JIM.Scim.Service;
using JIM.Web.Scim;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests.Scim;

/// <summary>
/// SCIM discovery (RFC 7644 section 4; #124 Phase 4b): what a Connected System's store publishes about itself, from
/// its own stored definitions (D18), with locations under its own base URL.
/// </summary>
[TestFixture]
public class ScimDiscoveryControllerTests
{
    private const int ConnectedSystemId = 7;
    private const string BaseUrl = "https://jim.example.com/scim/v2/7";

    [Test]
    public void ServiceProviderConfig_AdvertisesThePageCapAndItsLocation()
    {
        var body = Body(Controller().GetServiceProviderConfig());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body["filter"]?["maxResults"]?.GetValue<int>(), Is.EqualTo(1000));
            Assert.That(body["bulk"]?["supported"]?.GetValue<bool>(), Is.False);
            Assert.That(body["meta"]?["location"]?.GetValue<string>(), Is.EqualTo(BaseUrl + "/ServiceProviderConfig"));
        }
    }

    [Test]
    public async Task ResourceTypes_ListsTheStoresTypesAsAListResponseAsync()
    {
        var body = Body(await Controller().GetResourceTypesAsync(filter: null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body["schemas"]?[0]?.GetValue<string>(), Is.EqualTo(ScimUrns.ListResponse));
            Assert.That(body["totalResults"]?.GetValue<int>(), Is.EqualTo(2));
            Assert.That(body["Resources"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()), Is.EquivalentTo(new[] { "User", "Group" }));
        }
    }

    [Test]
    public async Task ResourceType_ById_MatchedWithoutRegardToCaseAsync()
    {
        var body = Body(await Controller().GetResourceTypeAsync("user"));

        Assert.That(body["meta"]?["location"]?.GetValue<string>(), Is.EqualTo(BaseUrl + "/ResourceTypes/User"));
    }

    [Test]
    public async Task ResourceType_Unknown_Scim404Async()
    {
        var result = await Controller().GetResourceTypeAsync("Device");

        Assert.That(StatusCode(result), Is.EqualTo(StatusCodes.Status404NotFound));
    }

    [Test]
    public async Task Schemas_ListsTheStoresSchemasAsync()
    {
        var body = Body(await Controller().GetSchemasAsync(filter: null));

        Assert.That(body["Resources"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()),
            Is.EquivalentTo(new[] { ScimUrns.User, ScimUrns.Group, ScimUrns.EnterpriseUser }));
    }

    [Test]
    public async Task Schema_ByUrn_ReturnedAsync()
    {
        var body = Body(await Controller().GetSchemaAsync(ScimUrns.Group));

        Assert.That(body["id"]?.GetValue<string>(), Is.EqualTo(ScimUrns.Group));
    }

    [Test]
    public async Task ResourceTypes_WithAFilter_Refused403Async()
    {
        // RFC 7644 section 4: discovery endpoints do not filter, and say so rather than ignore the filter.
        var result = await Controller().GetResourceTypesAsync(filter: "name eq \"User\"");

        Assert.That(StatusCode(result), Is.EqualTo(StatusCodes.Status403Forbidden));
    }

    private static ScimDiscoveryController Controller()
    {
        var (schemas, resourceTypes) = ScimCoreDefinitions.Rows();
        var store = new Mock<IScimStoreRepository>();
        store.Setup(s => s.GetResourceTypesAsync(ConnectedSystemId, It.IsAny<CancellationToken>())).ReturnsAsync(resourceTypes);
        store.Setup(s => s.GetSchemasAsync(ConnectedSystemId, It.IsAny<CancellationToken>())).ReturnsAsync(schemas);
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ScimStore).Returns(store.Object);

        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("jim.example.com");
        context.Request.Path = "/scim/v2/7/ResourceTypes";
        context.Request.RouteValues[ScimRoutes.ConnectedSystemIdRouteValue] = "7";

        return new ScimDiscoveryController(new JimApplication(repository.Object))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static int? StatusCode(IActionResult result) => (result as ContentResult)?.StatusCode;

    private static JsonNode Body(IActionResult result)
    {
        var content = (ContentResult)result;
        Assert.That(content.ContentType, Does.StartWith(ScimResponses.ContentType));
        return JsonNode.Parse(content.Content!)!;
    }
}
