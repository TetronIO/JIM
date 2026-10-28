// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Authentication;

namespace JIM.Web.Models.Api;

/// <summary>
/// The sign-in problems JIM has met since it started, and the redirect URIs it sends the identity provider.
/// </summary>
public class SignInDiagnosticsDto
{
    /// <summary>
    /// The redirect URI JIM sends the identity provider when reached at the address of this request. The identity
    /// provider must have it registered for the portal's client, or it shows its own error page and never returns to JIM.
    /// </summary>
    public required string RedirectUri { get; set; }

    /// <summary>
    /// The post-logout redirect URI JIM sends the identity provider when reached at the address of this request.
    /// </summary>
    public required string PostLogoutRedirectUri { get; set; }

    /// <summary>
    /// The sign-in problems JIM has met since it started, the one seen most recently first. Empty when there are none.
    /// </summary>
    public List<SignInProblemDto> Problems { get; set; } = [];

    /// <summary>
    /// Builds the diagnostics for a request.
    /// </summary>
    public static SignInDiagnosticsDto From(HttpRequest request, IEnumerable<SignInProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(problems);

        // The OpenID Connect handler's own paths, which JIM keeps.
        var jimUrl = $"{request.Scheme}://{request.Host}{request.PathBase}";
        return new SignInDiagnosticsDto
        {
            RedirectUri = $"{jimUrl}/signin-oidc",
            PostLogoutRedirectUri = $"{jimUrl}/signout-callback-oidc",
            Problems = problems.Select(SignInProblemDto.FromProblem).ToList()
        };
    }
}

/// <summary>
/// A sign-in failure a setting explains: the setting, the value JIM expected, the value it received, and where to fix
/// it. It never holds a token, a secret or a claim's value.
/// </summary>
public class SignInProblemDto
{
    /// <summary>
    /// Where JIM met it: <c>Portal</c> for someone signing in to the portal, <c>Api</c> for an access token presented to
    /// the API.
    /// </summary>
    public SignInProblemSource Source { get; set; }

    /// <summary>
    /// The setting involved, such as <c>JIM_SSO_API_SCOPE</c>.
    /// </summary>
    public required string Setting { get; set; }

    /// <summary>
    /// What went wrong, in a sentence.
    /// </summary>
    public required string Description { get; set; }

    /// <summary>
    /// The value JIM expected.
    /// </summary>
    public required string Expected { get; set; }

    /// <summary>
    /// The value JIM received.
    /// </summary>
    public required string Received { get; set; }

    /// <summary>
    /// Where to fix it.
    /// </summary>
    public required string Fix { get; set; }

    /// <summary>
    /// When JIM first met it since it started.
    /// </summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>
    /// When JIM last met it.
    /// </summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// How many times JIM has met it since it started.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Maps a problem JIM holds.
    /// </summary>
    public static SignInProblemDto FromProblem(SignInProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new SignInProblemDto
        {
            Source = problem.Source,
            Setting = problem.Setting,
            Description = problem.Description,
            Expected = problem.Expected,
            Received = problem.Received,
            Fix = problem.Fix,
            FirstSeenUtc = problem.FirstSeen.UtcDateTime,
            LastSeenUtc = problem.LastSeen.UtcDateTime,
            Count = problem.Count
        };
    }
}
