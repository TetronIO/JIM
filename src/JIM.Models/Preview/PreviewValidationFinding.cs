// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Preview;

/// <summary>
/// Something structurally wrong, contradictory, or worth knowing about a proposed configuration, found before any
/// population is evaluated. Stage 1 runs synchronously and near-instantly, so an administrator learns their proposal
/// is invalid immediately rather than after waiting for a preview that could never have been meaningful.
/// </summary>
/// <param name="Severity">
/// How much it matters. Only <see cref="PreviewValidationSeverity.Blocking"/> prevents the change being applied.
/// </param>
/// <param name="Message">
/// What is wrong, in the terms an administrator would use. Never contains attribute values or anything else that
/// counts as personal data; a finding is about the configuration, not about the objects it would affect.
/// </param>
/// <param name="PropertyName">
/// The configuration property concerned, where the finding is about one, so the editor can point at the field
/// rather than leaving the administrator to find it.
/// </param>
public record PreviewValidationFinding(
    PreviewValidationSeverity Severity,
    string Message,
    string? PropertyName = null);
