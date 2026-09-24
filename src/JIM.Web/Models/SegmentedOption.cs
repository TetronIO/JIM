// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// One option of a <c>SegmentedControl</c>: the value it selects and the label it shows.
/// </summary>
public sealed record SegmentedOption<TValue>(TValue Value, string Label);
