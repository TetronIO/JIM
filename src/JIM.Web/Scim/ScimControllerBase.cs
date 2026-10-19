// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json;
using JIM.Application;
using JIM.Scim.Serialisation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ScimErrorDocument = JIM.Scim.Messages.ScimError;

namespace JIM.Web.Scim;

/// <summary>
/// What every SCIM 2.0 Service Provider endpoint shares: the scoped key policy, SCIM JSON on the way out, and the
/// Connected System and base URL the route names. Deliberately not an <c>[ApiController]</c>: SCIM endpoints answer
/// with SCIM errors, never ProblemDetails or automatic model validation, and stay out of the REST API's versioning
/// and OpenAPI document.
/// </summary>
[Authorize(Policy = ScimRoutes.AuthorisationPolicy)]
[ApiExplorerSettings(IgnoreApi = true)]
public abstract class ScimControllerBase(JimApplication application) : ControllerBase
{
    /// <summary>
    /// Why each SCIM controller suppresses the API versioning analyser's request for <c>[ApiController]</c>: SCIM's
    /// URL carries its own version (<c>/scim/v2</c>), and the attribute would bring ProblemDetails and automatic
    /// model validation, which SCIM clients cannot read.
    /// </summary>
    public const string NotAnApiControllerJustification =
        "SCIM endpoints are versioned by their own /scim/v2 path and must answer with SCIM errors, never ProblemDetails.";

    protected JimApplication Application { get; } = application;

    /// <summary>
    /// The Connected System the route names. The route constrains it to an integer, and the policy has already matched
    /// it against the key.
    /// </summary>
    protected int ConnectedSystemId => ScimConnectedSystemScopeHandler.RouteConnectedSystemId(HttpContext)
        ?? throw new InvalidOperationException("A SCIM endpoint was reached without a Connected System in its route.");

    /// <summary>
    /// This Connected System's SCIM base URL as the client reached it, which every published location is built on.
    /// </summary>
    protected string BaseUrl =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}{ScimRoutes.PathBase}/v2/{ConnectedSystemId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// A SCIM JSON response, as <c>application/scim+json</c> unless the client accepts only <c>application/json</c>
    /// (RFC 7644 section 3.1 allows either).
    /// </summary>
    protected ContentResult Scim(object body, int statusCode = StatusCodes.Status200OK) => new()
    {
        Content = JsonSerializer.Serialize(body, body.GetType(), ScimJson.Options),
        ContentType = $"{ResponseMediaType()}; charset=utf-8",
        StatusCode = statusCode
    };

    /// <summary>
    /// A SCIM error response (RFC 7644 section 3.12).
    /// </summary>
    protected ContentResult ScimError(int statusCode, string? detail = null, string? scimType = null) =>
        Scim(ScimErrorDocument.ForStatus(statusCode, detail, scimType), statusCode);

    private string ResponseMediaType()
    {
        var accept = Request.Headers.Accept.ToString();
        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
               && !accept.Contains(ScimResponses.ContentType, StringComparison.OrdinalIgnoreCase)
            ? "application/json"
            : ScimResponses.ContentType;
    }
}
