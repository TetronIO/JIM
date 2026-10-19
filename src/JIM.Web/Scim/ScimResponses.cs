// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Scim.Messages;
using JIM.Scim.Serialisation;

namespace JIM.Web.Scim;

/// <summary>
/// Writes SCIM responses outside MVC: errors raised by the pipeline before or around the SCIM controllers.
/// </summary>
public static class ScimResponses
{
    /// <summary>
    /// The SCIM media type (RFC 7644 section 3.1).
    /// </summary>
    public const string ContentType = "application/scim+json";

    /// <summary>
    /// Writes a SCIM error (RFC 7644 section 3.12) with the given status.
    /// </summary>
    public static async Task WriteErrorAsync(HttpContext context, int statusCode, string? detail = null, string? scimType = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = $"{ContentType}; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, ScimError.ForStatus(statusCode, detail, scimType), ScimJson.Options,
            context.RequestAborted);
    }
}
