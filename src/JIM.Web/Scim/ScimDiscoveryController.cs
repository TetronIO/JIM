// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics.CodeAnalysis;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Messages;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Scim;

/// <summary>
/// SCIM discovery (RFC 7644 section 4): what this Connected System's store supports, which resource types it serves
/// and their schemas, all from the store's own definitions (D18). Authentication is required here as everywhere on
/// <c>/scim</c> (D3).
/// </summary>
[Route(ScimRoutes.BaseTemplate)]
[SuppressMessage("Asp.Versioning", "AV0014", Justification = ScimControllerBase.NotAnApiControllerJustification)]
public class ScimDiscoveryController(JimApplication application) : ScimControllerBase(application)
{
    private const string NotFiltered = "Discovery endpoints do not support filtering.";

    [HttpGet(ScimEndpoints.ServiceProviderConfig)]
    public IActionResult GetServiceProviderConfig() =>
        Scim(ScimServiceProviderDiscovery.ServiceProviderConfig(BaseUrl, ScimServer.MaxPageSize));

    [HttpGet(ScimEndpoints.ResourceTypes)]
    public async Task<IActionResult> GetResourceTypesAsync([FromQuery] string? filter)
    {
        if (filter != null)
            return ScimError(StatusCodes.Status403Forbidden, NotFiltered);

        return Scim(List(await ResourceTypesAsync()));
    }

    [HttpGet(ScimEndpoints.ResourceTypes + "/{id}")]
    public async Task<IActionResult> GetResourceTypeAsync(string id)
    {
        var resourceType = (await ResourceTypesAsync()).FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        return resourceType == null ? ScimError(StatusCodes.Status404NotFound, "No such resource type.") : Scim(resourceType);
    }

    [HttpGet(ScimEndpoints.Schemas)]
    public async Task<IActionResult> GetSchemasAsync([FromQuery] string? filter)
    {
        if (filter != null)
            return ScimError(StatusCodes.Status403Forbidden, NotFiltered);

        return Scim(List(await SchemasAsync()));
    }

    [HttpGet(ScimEndpoints.Schemas + "/{id}")]
    public async Task<IActionResult> GetSchemaAsync(string id)
    {
        var schema = (await SchemasAsync()).FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        return schema == null ? ScimError(StatusCodes.Status404NotFound, "No such schema.") : Scim(schema);
    }

    private async Task<List<ScimResourceType>> ResourceTypesAsync()
    {
        var (resourceTypes, _) = await Application.Scim.GetDiscoveryDocumentsAsync(ConnectedSystemId, HttpContext.RequestAborted);
        return ScimServiceProviderDiscovery.ResourceTypes(BaseUrl, resourceTypes);
    }

    private async Task<List<ScimSchema>> SchemasAsync()
    {
        var (_, schemas) = await Application.Scim.GetDiscoveryDocumentsAsync(ConnectedSystemId, HttpContext.RequestAborted);
        return ScimServiceProviderDiscovery.Schemas(BaseUrl, schemas);
    }

    private static ScimListResponse<T> List<T>(List<T> resources) => new()
    {
        TotalResults = resources.Count,
        ItemsPerPage = resources.Count,
        StartIndex = 1,
        Resources = resources
    };
}
