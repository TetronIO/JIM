// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Security;
using JIM.Web.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JIM.Web.Pages;

/// <summary>
/// Explains why nobody can sign in when JIM cannot reach or trust its identity provider, and shows the command that
/// fixes the usual case (#2363). Anonymous by necessity. It only ever reads: trusting a certificate takes an
/// administrator, signed in or holding the infrastructure API key, never a visitor to this page.
/// </summary>
[AllowAnonymous]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public class SignInUnavailableModel(IIdentityProviderTrustMonitor monitor) : PageModel
{
    public IdentityProviderTrustStatus Status { get; private set; } = new();

    /// <summary>
    /// Whether sign-in works again (or never depended on a certificate), so the page offers to sign in.
    /// </summary>
    public bool IsAvailable => Status.State is IdentityProviderTrustState.Trusted or IdentityProviderTrustState.NotHttps;

    /// <summary>
    /// Whether trusting a certificate the identity provider presents would fix it.
    /// </summary>
    public bool CanTrustFromIdentityProvider => Status.State == IdentityProviderTrustState.Untrusted && Status.SuggestedCertificate != null;

    /// <summary>
    /// The PowerShell that trusts the suggested certificate, using the infrastructure API key, ready to copy.
    /// </summary>
    public string TrustCommand { get; private set; } = string.Empty;

    public string DocsUrl => IdentityProviderTrustMonitor.DocsUrl;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Status = await monitor.GetFreshStatusAsync(cancellationToken);

        var jimUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        TrustCommand = CanTrustFromIdentityProvider
            ? $"Connect-JIM -Url {jimUrl} -ApiKey $env:JIM_INFRASTRUCTURE_API_KEY\nAdd-JIMCertificate -FromIdentityProvider -Thumbprint {Status.SuggestedCertificate!.Thumbprint}"
            : string.Empty;
    }
}
