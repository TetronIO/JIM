// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// What to do when a save stops a configuration test case passing (#2055 Phase 4b, the save gate). With neither set,
/// such a save is refused and changes nothing.
/// </summary>
/// <param name="SaveAnyway">Save even though the cases no longer pass, recording that in Configuration History.</param>
/// <param name="AcceptNewResults">Take what the configuration now gives as what each case that stopped passing
/// expects, and save. A case whose new result cannot be expected (it needs review, could not run, or its Scope cannot
/// be determined) still refuses the save unless <paramref name="SaveAnyway"/> is set too.</param>
public sealed record ConfigurationTestGateOptions(bool SaveAnyway = false, bool AcceptNewResults = false)
{
    /// <summary>Refuse a save that stops a case passing.</summary>
    public static ConfigurationTestGateOptions None { get; } = new();
}

/// <summary>
/// A configuration test case that passed against the configuration as saved, and does not pass against the
/// configuration a save proposes.
/// </summary>
public sealed class ConfigurationTestRegression
{
    public required ConfigurationTestCase TestCase { get; init; }

    /// <summary>The case's run against the configuration as saved: always <see cref="ConfigurationTestStatus.Passed"/>.</summary>
    public required ConfigurationTestRunResult Before { get; init; }

    /// <summary>The case's run against the configuration the save proposes.</summary>
    public required ConfigurationTestRunResult After { get; init; }

    /// <summary>
    /// What the case would expect if its new result were accepted, or null when the new result cannot be expected:
    /// the case needs review, could not run, its Scope cannot be determined, or the result is larger than a case can
    /// expect.
    /// </summary>
    public ConfigurationTestExpectation? AcceptedExpectation { get; init; }

    /// <summary>True when the new result can be accepted as what the case expects.</summary>
    public bool CanAccept => AcceptedExpectation != null;
}

/// <summary>
/// What the save gate decided for a save that went ahead: the cases whose new result was accepted, and those saved past.
/// </summary>
public sealed class ConfigurationTestGateOutcome
{
    public static ConfigurationTestGateOutcome Nothing { get; } = new();

    /// <summary>Cases whose new result the save takes as what they expect; each is rewritten with the save.</summary>
    public IReadOnlyList<ConfigurationTestRegression> Accepted { get; init; } = [];

    /// <summary>Cases that no longer pass, saved past at the administrator's request.</summary>
    public IReadOnlyList<ConfigurationTestRegression> SavedPast { get; init; } = [];

    public bool IsEmpty => Accepted.Count == 0 && SavedPast.Count == 0;

    /// <summary>
    /// The note recorded with the change in Configuration History: each case's name and status, never its values. Null
    /// when the gate let the save through untouched.
    /// </summary>
    public string? Note
    {
        get
        {
            if (IsEmpty)
                return null;

            var sentences = new List<string>();
            if (SavedPast.Count > 0)
                sentences.Add($"Saved past {Count(SavedPast.Count)} that no longer {(SavedPast.Count == 1 ? "passes" : "pass")}: " +
                              $"{string.Join(", ", SavedPast.Select(r => $"'{r.TestCase.Name}' ({Describe(r.After.Status)})"))}.");
            if (Accepted.Count > 0)
                sentences.Add($"Accepted the new result as expected for {Count(Accepted.Count)}: " +
                              $"{string.Join(", ", Accepted.Select(r => $"'{r.TestCase.Name}'"))}.");
            return string.Join(" ", sentences);
        }
    }

    private static string Count(int count) => count == 1 ? "1 configuration test case" : $"{count} configuration test cases";

    private static string Describe(ConfigurationTestStatus status) => status switch
    {
        ConfigurationTestStatus.Failed => "failed",
        ConfigurationTestStatus.NeedsReview => "needs review",
        ConfigurationTestStatus.Error => "could not run",
        _ => "passed"
    };
}
