// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JIM.Web.Pages;

/// <summary>
/// Explains a portal sign-in a setting stopped (#2443): the setting, the value JIM expected and the value it received.
/// Anonymous by necessity: whoever lands here could not sign in. It shows only portal problems, whose values passed
/// through the visitor's own browser, and only the one the failed sign-in named.
/// </summary>
[AllowAnonymous]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public class SignInProblemModel(SignInProblemLog problems) : PageModel
{
    public const string DocsUrl = "https://docs.junctional.io/administration/troubleshooting/#a-setting-stopped-sign-in";

    /// <summary>The problem to explain, or null when JIM no longer holds it.</summary>
    public SignInProblem? Problem { get; private set; }

    public void OnGet(Guid? id)
    {
        Problem = id is { } problemId && problems.Find(problemId) is { Source: SignInProblemSource.Portal } problem ? problem : null;
    }
}
