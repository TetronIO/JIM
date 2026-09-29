// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Shared;

/// <summary>
/// One option of a <see cref="SegmentedToggle{TValue}"/>: the value it chooses, the label shown, and an optional
/// test id for the option's button.
/// </summary>
public sealed record SegmentedToggleOption<TValue>(TValue Value, string Label, string? TestId = null);
