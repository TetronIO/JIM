// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using JIM.Application;
using JIM.Scim;
using JIM.Scim.Messages;
using JIM.Scim.Serialisation;
using JIM.Scim.Service;
using JIM.Web.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Scim;

/// <summary>
/// The SCIM resource endpoints (RFC 7644 section 3): create, read, replace, patch, delete, list and search, for every
/// resource type the store's definitions declare (D18), User and Group today. The endpoint is a route value rather than
/// a fixed path, so a custom resource type is served without a controller of its own. The HTTP surface only translates:
/// every rule (intake, uniqueness, references, concurrency, the journal) is the SCIM server's, and every write is
/// attributed to the authenticated key and client address (D17).
/// </summary>
[Route(ScimRoutes.BaseTemplate)]
[SuppressMessage("Asp.Versioning", "AV0014", Justification = NotAnApiControllerJustification)]
public class ScimResourcesController(JimApplication application) : ScimControllerBase(application)
{
    private const string NotAnObject = "The request body must be a JSON object.";

    [HttpPost("{endpoint}")]
    public async Task<IActionResult> CreateAsync(string endpoint)
    {
        var (body, error) = await ReadBodyAsync();
        if (error != null)
            return error;

        return Respond(await Application.Scim.CreateAsync(RequestContext(), endpoint, body!, SelectionFromQuery()));
    }

    [HttpGet("{endpoint}/{id}")]
    public async Task<IActionResult> GetAsync(string endpoint, string id) =>
        Respond(await Application.Scim.GetAsync(RequestContext(), endpoint, id, SelectionFromQuery()));

    [HttpGet("{endpoint}")]
    public async Task<IActionResult> ListAsync(string endpoint)
    {
        var (request, error) = ListRequestFromQuery();
        if (error != null)
            return error;

        return Respond(await Application.Scim.ListAsync(RequestContext(), endpoint, request!));
    }

    [HttpPost("{endpoint}/" + ScimEndpoints.Search)]
    public async Task<IActionResult> SearchAsync(string endpoint)
    {
        var (body, error) = await ReadBodyAsync();
        if (error != null)
            return error;

        var (request, invalid) = ListRequestFromBody(body!);
        if (invalid != null)
            return invalid;

        return Respond(await Application.Scim.ListAsync(RequestContext(), endpoint, request!));
    }

    [HttpPut("{endpoint}/{id}")]
    public async Task<IActionResult> ReplaceAsync(string endpoint, string id)
    {
        var (body, error) = await ReadBodyAsync();
        if (error != null)
            return error;

        return Respond(await Application.Scim.ReplaceAsync(RequestContext(), endpoint, id, body!, IfMatch(), SelectionFromQuery()));
    }

    [HttpPatch("{endpoint}/{id}")]
    public async Task<IActionResult> PatchAsync(string endpoint, string id)
    {
        var (body, error) = await ReadBodyAsync();
        if (error != null)
            return error;

        ScimPatchRequest? patch;
        try
        {
            patch = body.Deserialize<ScimPatchRequest>(ScimJson.Options);
        }
        catch (JsonException)
        {
            patch = null;
        }

        if (patch == null || patch.Operations.Count == 0)
            return ScimError(StatusCodes.Status400BadRequest, "A PATCH request needs at least one operation in 'Operations'.",
                ScimErrorTypes.InvalidSyntax);

        return Respond(await Application.Scim.PatchAsync(RequestContext(), endpoint, id, patch.Operations, IfMatch(), SelectionFromQuery()));
    }

    [HttpDelete("{endpoint}/{id}")]
    public async Task<IActionResult> DeleteAsync(string endpoint, string id) =>
        Respond(await Application.Scim.DeleteAsync(RequestContext(), endpoint, id, IfMatch()));

    /// <summary>
    /// The request's actor for the journal: the authenticated key, by id and name, and the client's address.
    /// </summary>
    private ScimRequestContext RequestContext() => new(
        ConnectedSystemId,
        BaseUrl,
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var keyId) ? keyId : null,
        User.FindFirstValue(ClaimTypes.Name),
        HttpContext.Connection.RemoteIpAddress.ToNormalisedString());

    private IActionResult Respond(ScimServiceResult result)
    {
        if (result.ETag is { } etag)
            Response.Headers.ETag = etag;
        if (result.Location is { } location)
            Response.Headers.Location = location;

        if (result.Error is { } error)
        {
            HttpContext.Items[ScimRequestOutcomes.ErrorTypeItem] = error.ScimType;
            return Scim(error, result.StatusCode);
        }
        return result.Resource is { } resource ? Scim(resource, result.StatusCode) : new StatusCodeResult(result.StatusCode);
    }

    /// <summary>
    /// Reads the body as a JSON object. Read by hand rather than bound, so that anything else is a SCIM
    /// <c>invalidSyntax</c> error, never the framework's own error format.
    /// </summary>
    private async Task<(JsonObject? Body, ContentResult? Error)> ReadBodyAsync()
    {
        try
        {
            if (await JsonNode.ParseAsync(Request.Body, cancellationToken: HttpContext.RequestAborted) is JsonObject body)
                return (body, null);
        }
        catch (JsonException)
        {
            // Falls through to the error below: not JSON at all, or empty.
        }

        return (null, ScimError(StatusCodes.Status400BadRequest, NotAnObject, ScimErrorTypes.InvalidSyntax));
    }

    private string? IfMatch() => Request.Headers.IfMatch.Count > 0 ? Request.Headers.IfMatch.ToString() : null;

    private ScimAttributeSelection? SelectionFromQuery() =>
        Selection(Names(Request.Query["attributes"].ToString()), Names(Request.Query["excludedAttributes"].ToString()));

    private (ScimListRequest? Request, ContentResult? Error) ListRequestFromQuery()
    {
        var query = Request.Query;
        if (!TryInt(query["startIndex"].ToString(), "startIndex", out var startIndex, out var error)
            || !TryInt(query["count"].ToString(), "count", out var count, out error))
            return (null, error);

        // A cursor parameter present but empty asks for the first page with cursor paging (RFC 9865).
        var cursor = query.ContainsKey("cursor") ? query["cursor"].ToString() : null;
        var filter = query.ContainsKey("filter") ? query["filter"].ToString() : null;
        return (new ScimListRequest(filter, startIndex, count, cursor, SelectionFromQuery()), null);
    }

    private (ScimListRequest? Request, ContentResult? Error) ListRequestFromBody(JsonObject body)
    {
        var values = new Dictionary<string, JsonNode?>(body, StringComparer.OrdinalIgnoreCase);
        try
        {
            return (new ScimListRequest(
                Text(values, "filter"),
                Number(values, "startIndex"),
                Number(values, "count"),
                Text(values, "cursor"),
                Selection(NamesOf(values, "attributes"), NamesOf(values, "excludedAttributes"))), null);
        }
        catch (InvalidOperationException)
        {
            // A member of the wrong JSON kind: a number where a string belongs, an object where a list does.
            return (null, WrongMemberType());
        }
        catch (FormatException)
        {
            return (null, WrongMemberType());
        }
    }

    private ContentResult WrongMemberType() =>
        ScimError(StatusCodes.Status400BadRequest, "The search request has a member of the wrong type.", ScimErrorTypes.InvalidValue);

    private static ScimAttributeSelection? Selection(IReadOnlyCollection<string>? attributes, IReadOnlyCollection<string>? excluded) =>
        attributes == null && excluded == null ? null : new ScimAttributeSelection(attributes, excluded);

    private static List<string>? Names(string commaSeparated)
    {
        var names = commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return names.Count == 0 ? null : names;
    }

    // RFC 7644 section 3.4.3 gives a search's attributes as an array; a comma-separated string is tolerated too.
    private static List<string>? NamesOf(Dictionary<string, JsonNode?> values, string name) =>
        values.GetValueOrDefault(name) switch
        {
            null => null,
            JsonArray array => array.Select(n => n!.GetValue<string>()).ToList() is { Count: > 0 } list ? list : null,
            var single => Names(single.GetValue<string>())
        };

    private static string? Text(Dictionary<string, JsonNode?> values, string name) => values.GetValueOrDefault(name)?.GetValue<string>();

    private static int? Number(Dictionary<string, JsonNode?> values, string name) => values.GetValueOrDefault(name)?.GetValue<int>();

    private bool TryInt(string text, string name, out int? value, out ContentResult? error)
    {
        value = null;
        error = null;
        if (text.Length == 0)
            return true;
        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        error = ScimError(StatusCodes.Status400BadRequest, $"'{name}' must be an integer.", ScimErrorTypes.InvalidValue);
        return false;
    }
}
