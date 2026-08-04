// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.SCIM.Authentication;

/// <summary>
/// Thrown when the connector cannot obtain the credential it needs to call a service provider.
/// <para>
/// Messages describe the failure (status code, missing field) and deliberately never carry the
/// configured secret, the acquired token, or the authorisation server's raw response body, since these
/// propagate into Activity errors, RPEIs and logs.
/// </para>
/// </summary>
public class ScimAuthenticationException : Exception
{
    public ScimAuthenticationException(string message) : base(message)
    {
    }

    public ScimAuthenticationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
