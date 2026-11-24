// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

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

    /// <summary>
    /// A failure whose words are held as a message, so the portal can show them in the viewer's language (#3030);
    /// <see cref="Exception.Message"/> is its British English, which Activities and logs keep reading.
    /// </summary>
    public HttpAuthenticationException(JimMessage detail) : base(detail.ToEnglish())
    {
        Detail = detail;
    }

    /// <summary>The failure as a message the portal can render in the viewer's language; null where only English text exists.</summary>
    public JimMessage? Detail { get; }
}
