// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Models.Exceptions;

/// <summary>
/// A save was refused because it stops one or more configuration test cases passing (#2055 Phase 4b, the save gate).
/// Nothing was saved. Save again with <see cref="ConfigurationTestGateOptions"/> to accept the new results or to save
/// anyway. The message names the cases, never their values.
/// </summary>
public class ConfigurationTestsRegressedException : Exception
{
    public ConfigurationTestsRegressedException(IReadOnlyList<ConfigurationTestRegression> regressions)
        : base(Describe(regressions))
    {
        Regressions = regressions;
    }

    /// <summary>Each case that passed before the save and would not after it.</summary>
    public IReadOnlyList<ConfigurationTestRegression> Regressions { get; }

    private static string Describe(IReadOnlyList<ConfigurationTestRegression> regressions)
    {
        var names = string.Join(", ", regressions.Select(r => $"'{r.TestCase.Name}'"));
        return regressions.Count == 1
            ? $"This change stops the configuration test case {names} passing. Keep editing, accept the new result, or save anyway."
            : $"This change stops {regressions.Count} configuration test cases passing: {names}. Keep editing, accept the new results, or save anyway.";
    }
}
