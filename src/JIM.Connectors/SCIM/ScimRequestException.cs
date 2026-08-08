// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using JIM.Scim.Messages;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Thrown when a SCIM request finally fails, after any retries the policy allowed.
/// <para>
/// Carries the HTTP status and, where the provider returned a conformant SCIM error, the parsed body.
/// Callers use these to react per case: 404 on a delete may be benign, 409 <c>uniqueness</c> is a
/// matching problem, and 400 <c>invalidValue</c> on a reference indicates export dependency ordering.
/// </para>
/// </summary>
public class ScimRequestException : Exception
{
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// The parsed SCIM error, or null when the provider returned something else (an HTML error page from
    /// an intermediary, for example).
    /// </summary>
    public ScimError? Error { get; }

    /// <summary>
    /// The canonical SCIM error keyword, when the provider supplied one. See <see cref="ScimErrorTypes"/>.
    /// </summary>
    public string? ScimType => Error?.ScimType;

    public ScimRequestException(string message, HttpStatusCode statusCode, ScimError? error = null)
        : base(message)
    {
        StatusCode = statusCode;
        Error = error;
    }

    public ScimRequestException(string message, HttpStatusCode statusCode, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
