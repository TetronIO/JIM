// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Authentication;

/// <summary>
/// Where JIM met a sign-in problem.
/// </summary>
public enum SignInProblemSource
{
    /// <summary>Someone signing in to the portal.</summary>
    Portal,

    /// <summary>An access token presented to the API, by the PowerShell module or another client.</summary>
    Api
}

/// <summary>
/// A sign-in failure a setting explains (#2443): the setting involved, the value JIM expected and the value it received,
/// and where to fix it. It never holds a token, a secret or a claim's value.
/// </summary>
/// <param name="Source">Where the failure was met.</param>
/// <param name="Setting">The setting involved, such as <c>JIM_SSO_API_SCOPE</c>.</param>
/// <param name="Description">What went wrong, in a sentence.</param>
/// <param name="Expected">The value JIM expected.</param>
/// <param name="Received">The value JIM received.</param>
/// <param name="Fix">Where to fix it.</param>
public sealed record SignInProblemReport(SignInProblemSource Source, string Setting, string Description, string Expected, string Received, string Fix);

/// <summary>
/// A sign-in problem JIM has met, with how often and when.
/// </summary>
public sealed record SignInProblem
{
    /// <summary>Identifies the problem while JIM holds it, so the page explaining a failed sign-in can show it.</summary>
    public required Guid Id { get; init; }

    /// <inheritdoc cref="SignInProblemReport.Source"/>
    public required SignInProblemSource Source { get; init; }

    /// <inheritdoc cref="SignInProblemReport.Setting"/>
    public required string Setting { get; init; }

    /// <inheritdoc cref="SignInProblemReport.Description"/>
    public required string Description { get; init; }

    /// <inheritdoc cref="SignInProblemReport.Expected"/>
    public required string Expected { get; init; }

    /// <inheritdoc cref="SignInProblemReport.Received"/>
    public required string Received { get; init; }

    /// <inheritdoc cref="SignInProblemReport.Fix"/>
    public required string Fix { get; init; }

    /// <summary>When JIM first met the problem since it started.</summary>
    public required DateTimeOffset FirstSeen { get; init; }

    /// <summary>When JIM last met the problem.</summary>
    public required DateTimeOffset LastSeen { get; init; }

    /// <summary>How many times JIM has met the problem since it started.</summary>
    public required int Count { get; init; }

    /// <summary>When the problem was last written to the log.</summary>
    public required DateTimeOffset LoggedAt { get; init; }
}
