// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What a single preflight check found.
/// </summary>
public class PasswordPreflightCheckResult
{
    /// <summary>
    /// The question this result answers.
    /// </summary>
    public PasswordPreflightCheck Check { get; init; }

    /// <summary>
    /// What the check established, if anything.
    /// </summary>
    public PasswordPreflightState State { get; init; }

    /// <summary>
    /// A plain statement of what was found, written for the administrator who has to act on it. Where the state is
    /// anything other than <see cref="PasswordPreflightState.Passed"/>, this says what to change.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Supporting lines, where one sentence cannot carry the answer. A rights check spanning several containers
    /// reports its per-container findings here, because "some of them" is the answer an administrator most needs
    /// broken down.
    /// </summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>
    /// Creates a result for a check that found nothing to worry about.
    /// </summary>
    public static PasswordPreflightCheckResult Passed(PasswordPreflightCheck check, string message, IReadOnlyList<string>? details = null) =>
        new() { Check = check, State = PasswordPreflightState.Passed, Message = message, Details = details ?? [] };

    /// <summary>
    /// Creates a result for a check that found something workable but ill-advised.
    /// </summary>
    public static PasswordPreflightCheckResult Warning(PasswordPreflightCheck check, string message, IReadOnlyList<string>? details = null) =>
        new() { Check = check, State = PasswordPreflightState.Warning, Message = message, Details = details ?? [] };

    /// <summary>
    /// Creates a result for a check that established password setting will not work as configured.
    /// </summary>
    public static PasswordPreflightCheckResult Failed(PasswordPreflightCheck check, string message, IReadOnlyList<string>? details = null) =>
        new() { Check = check, State = PasswordPreflightState.Failed, Message = message, Details = details ?? [] };

    /// <summary>
    /// Creates a result for a check that could not see enough to answer. The message must say why, because
    /// "unknown" without a reason gives the administrator nothing to act on.
    /// </summary>
    public static PasswordPreflightCheckResult CouldNotDetermine(PasswordPreflightCheck check, string message, IReadOnlyList<string>? details = null) =>
        new() { Check = check, State = PasswordPreflightState.CouldNotDetermine, Message = message, Details = details ?? [] };
}
