// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Scim.Messages;
using JIM.Scim.Resources;

namespace JIM.Scim.Service;

/// <summary>
/// What the SCIM server answers a request with, for the HTTP surface to send: a status, and the resource or the SCIM
/// error. Errors name attributes and never repeat a value.
/// </summary>
public sealed class ScimServiceResult
{
    private ScimServiceResult(int statusCode, JsonObject? resource, ScimError? error, long? version, string? location)
    {
        StatusCode = statusCode;
        Resource = resource;
        Error = error;
        Version = version;
        Location = location;
    }

    /// <summary>
    /// The HTTP status: 200, 201 or 204 on success, otherwise the error's.
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    /// The resource to return, projected for the request; null for a 204 and for an error.
    /// </summary>
    public JsonObject? Resource { get; }

    /// <summary>
    /// The SCIM error to return; null on success.
    /// </summary>
    public ScimError? Error { get; }

    /// <summary>
    /// The resource's version after the request, for the <c>ETag</c> header; null where there is no resource to tag.
    /// </summary>
    public long? Version { get; }

    /// <summary>
    /// The resource's location, for a 201's <c>Location</c> header.
    /// </summary>
    public string? Location { get; }

    /// <summary>
    /// The <c>ETag</c> header value, or null.
    /// </summary>
    public string? ETag => Version is { } version ? ScimETag.For(version) : null;

    public static ScimServiceResult Created(JsonObject resource, long version, string location) => new(201, resource, null, version, location);

    public static ScimServiceResult Ok(JsonObject resource, long version) => new(200, resource, null, version, null);

    public static ScimServiceResult NoContent() => new(204, null, null, null, null);

    public static ScimServiceResult Failed(ScimError error) => new(error.StatusCode ?? 500, null, error, null, null);

    public static ScimServiceResult NotFound(string detail) => Failed(ScimError.ForStatus(404, detail));
}
