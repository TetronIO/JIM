// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;

namespace JIM.Connectors.Graph;

/// <summary>
/// A Microsoft Graph request that failed and will not be retried: refused, or still failing once retries were spent.
/// Carries what Graph said, so a caller can tell a missing object from a refused permission, and the request id
/// Microsoft support asks for.
/// </summary>
public class GraphRequestException : Exception
{
    public GraphRequestException(string message, HttpStatusCode statusCode, string? errorCode = null, string? requestId = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        RequestId = requestId;
    }

    /// <summary>The HTTP status Graph answered with, or 503 where no response arrived.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Graph's own error code, i.e. <c>Request_ResourceNotFound</c> or <c>Authorization_RequestDenied</c>.</summary>
    public string? ErrorCode { get; }

    /// <summary>The <c>request-id</c> Graph assigned, for a support case with Microsoft.</summary>
    public string? RequestId { get; }
}
