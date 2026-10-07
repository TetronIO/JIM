// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// What allowing the rename of one held generated value would do (Unique Value Generation, #242, release 4, Phase 9):
/// the "Allow the rename" confirmation. The new value itself is decided by the worker at the next export, after it has
/// checked every system again; <see cref="LikelyValue"/> is only an expectation.
/// </summary>
public class GeneratedValueRenamePreview
{
    /// <summary>The held value.</summary>
    public GeneratedValueDecisionHeader Decision { get; set; } = null!;

    /// <summary>Every Connected System the value is exported to where the object has an account, and what happens there.</summary>
    public List<GeneratedValueRenameChange> Changes { get; set; } = [];

    /// <summary>
    /// The value JIM is likely to choose, where the flow's uniqueness token makes that cheap to say (a number or letter
    /// added only if the value is taken, from a known base). Null otherwise: a sequence or random token is not predicted.
    /// </summary>
    public string? LikelyValue { get; set; }
}
