// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// A criterion's answer, and what decided it: how many values were read before it was decided, whether one value
/// decided it, and the last value read (the deciding one when <see cref="Decided"/>).
/// </summary>
internal readonly record struct CriterionVerdict(bool Met, int HeldCount, bool Decided, ScopingValue LastHeld);
