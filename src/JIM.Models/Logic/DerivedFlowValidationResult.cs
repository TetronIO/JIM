// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// The outcome of validating a proposed save against the Metaverse-Derived Attribute Flow dependency graph (#1750):
/// errors refuse the save; warnings are reported alongside a save that goes ahead.
/// </summary>
public sealed class DerivedFlowValidationResult
{
    /// <summary>
    /// Administrator-facing reasons the save must be refused. Empty when the proposal is valid.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>
    /// Non-blocking warnings, each about one proposed mapping.
    /// </summary>
    public IReadOnlyList<DerivedFlowWarning> Warnings { get; }

    /// <summary>
    /// Whether the save must be refused.
    /// </summary>
    public bool HasErrors => Errors.Count > 0;

    public DerivedFlowValidationResult(IReadOnlyList<string> errors, IReadOnlyList<DerivedFlowWarning> warnings)
    {
        Errors = errors;
        Warnings = warnings;
    }
}
