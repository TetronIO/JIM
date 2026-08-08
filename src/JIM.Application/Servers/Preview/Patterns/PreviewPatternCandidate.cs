// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Servers.Preview.Patterns;

/// <summary>
/// One change put to the pattern detectors (#827 Phase 4b).
///
/// Deliberately narrower than a <see cref="JIM.Models.Preview.PreviewDelta"/>: a detector answers "what kind of edit
/// is this" and has no business seeing which object it belongs to. Keeping the identifiers out means a detector
/// cannot come to depend on them, and cannot leak them.
/// </summary>
/// <param name="AttributeName">
/// The attribute the change concerns, where it concerns one. Present because a future detector may need it to
/// disambiguate; the curated set reads shape alone, so any of them would answer the same without it.
/// </param>
/// <param name="OldValue">
/// The value now. Personal data: a detector may read it, but must never log it or put it in an exception message.
/// </param>
/// <param name="NewValue">The value the proposed configuration would produce. Personal data, as above.</param>
public record PreviewPatternCandidate(string? AttributeName, string? OldValue, string? NewValue);
