// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Http;

/// <summary>
/// Thrown when the connector cannot obtain the credential it needs to call a service provider.
/// <para>
/// Messages describe the failure (status code, missing field) and deliberately never carry the
/// configured secret, the acquired token, or the authorisation server's raw response body, since these
/// propagate into Activity errors, RPEIs and logs.
/// </para>
/// </summary>
public class HttpAuthenticationException : Exception
{
    public HttpAuthenticationException(string message) : base(message)
    {
    }

    public HttpAuthenticationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
