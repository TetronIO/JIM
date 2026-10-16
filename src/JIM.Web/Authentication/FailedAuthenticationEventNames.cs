// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Authentication;

/// <summary>
/// The event classes a failed authentication is recorded under; each becomes the audit Activity's name.
/// </summary>
public static class FailedAuthenticationEventNames
{
    /// <summary>
    /// An API key presented as a bearer token was unknown, disabled or expired.
    /// </summary>
    public const string ApiKey = "API key authentication failed";

    /// <summary>
    /// An SSO access token presented as a bearer token failed validation.
    /// </summary>
    public const string AccessToken = "Access token authentication failed";

    /// <summary>
    /// An interactive (portal) sign-in failed at the identity provider or on return to JIM.
    /// </summary>
    public const string InteractiveSignIn = "Interactive sign-in failed";
}
