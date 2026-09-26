// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JIM.Web.Pages;

/// <summary>
/// Explains a sign-in that <see cref="OidcSignInRecovery"/> stopped rather than restart forever. Anonymous by
/// necessity: whoever lands here has just failed to sign in.
/// </summary>
[AllowAnonymous]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public class SignInFailedModel : PageModel
{
    /// <summary>
    /// Whether this page itself was reached over plain HTTP. The recovery redirects here on the same origin as
    /// the failed callback, so the page's own request shows the transport the sign-in was attempted over.
    /// </summary>
    public bool IsPlainHttp { get; private set; }

    public string TlsAndReverseProxyDocsUrl => OidcSignInRecovery.TlsAndReverseProxyDocsUrl;

    public string SignInLoopsDocsUrl => OidcSignInRecovery.SignInLoopsDocsUrl;

    public void OnGet()
    {
        IsPlainHttp = !Request.IsHttps;
    }
}
