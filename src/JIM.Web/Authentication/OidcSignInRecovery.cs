// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace JIM.Web.Authentication;

/// <summary>
/// Decides how a remote OIDC sign-in failure is handled. Some failures are fully recovered by simply signing
/// in again: a replayed <c>/signin-oidc</c> callback (back button, restored tab, a refreshed error page)
/// carries a single-use authorisation code the IdP has already spent, and a lost correlation cookie has the
/// same shape. Those restart the sign-in from a clean URL instead of surfacing an exception page. Failures a
/// retry cannot fix (the IdP refused the user, or the configuration is wrong) are deliberately NOT restarted:
/// redirecting them would replace a loud, diagnosable error with a silent loop between JIM and the provider.
/// </summary>
public static class OidcSignInRecovery
{
    /// <summary>
    /// Whether the failure is one a fresh sign-in attempt recovers completely.
    /// </summary>
    public static bool ShouldRestartSignIn(Exception? failure)
    {
        if (failure == null)
            return false;

        // A spent or expired single-use authorisation code comes back from the token endpoint as
        // invalid_grant; anything else an OpenIdConnectProtocolException carries (access_denied,
        // invalid_client, ...) describes a decision or a misconfiguration a retry will only repeat.
        if (failure is OpenIdConnectProtocolException)
            return failure.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);

        // The callback arrived without the correlation cookie its challenge set, or with none of the
        // protocol fields at all (a bookmark or history navigation straight to /signin-oidc); the round
        // trip is broken but nothing is wrong with the user or the configuration, so a new one succeeds.
        return failure.Message.Contains("Correlation failed", StringComparison.OrdinalIgnoreCase)
               || failure.Message.Contains("message.State is null or empty", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The path to restart the sign-in from: the flow's original local return path when there is one, else
    /// the root. Anything that could leave the site (absolute or protocol-relative) falls back to the root,
    /// so the redirect can never be turned into an off-host jump.
    /// </summary>
    public static string GetSafeReturnPath(string? redirectUri)
    {
        return !string.IsNullOrEmpty(redirectUri)
               && redirectUri.StartsWith('/')
               && !redirectUri.StartsWith("//", StringComparison.Ordinal)
            ? redirectUri
            : "/";
    }
}
